-- The Inbox (docs/architect/inbox.md): agentd's messages to people, one thread per source (job, idea, review, chat,
-- watch). Whether a thread waits on a person is read live from its source, never guessed from the messages.

CREATE OR REPLACE FUNCTION agentd.inbox_add(
    p_source text, p_source_id bigint, p_title text, p_recipient text, p_kind text, p_markdown text, p_options jsonb)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_id bigint;
BEGIN
    INSERT INTO agentd.inbox_items (source, source_id, title, recipient, kind, markdown, options)
    VALUES (p_source, p_source_id, p_title, p_recipient, p_kind, p_markdown,
            CASE WHEN jsonb_typeof(p_options) = 'array' AND jsonb_array_length(p_options) > 0 THEN p_options END)
    RETURNING id INTO v_id;

    -- Ids only: the web UI re-reads the Inbox (the text never goes into the event log).
    INSERT INTO agentd.events (job_id, type, payload)
    VALUES (NULL, 'inbox.added', jsonb_build_object('id', v_id, 'source', p_source, 'sourceId', p_source_id, 'kind', p_kind));
    RETURN v_id;
END
$$;

-- Whether the source waits on a person now.
CREATE OR REPLACE FUNCTION agentd.inbox_waiting(p_source text, p_source_id bigint)
RETURNS boolean
LANGUAGE sql STABLE
AS $$
    SELECT CASE p_source
        WHEN 'job' THEN EXISTS (SELECT 1 FROM agentd.jobs AS j WHERE j.id = p_source_id AND j.state = 'WaitingForHuman')
                     OR EXISTS (SELECT 1 FROM agentd.permission_requests AS r WHERE r.job_id = p_source_id AND r.status = 'pending')
        WHEN 'idea' THEN EXISTS (SELECT 1 FROM agentd.ideas AS i WHERE i.id = p_source_id
                                   AND (i.status = 'Proposed' OR (i.status IN ('Created', 'Discarded') AND i.thread_id IS NOT NULL)))
        WHEN 'review' THEN EXISTS (SELECT 1 FROM agentd.pr_reviews AS r WHERE r.id = p_source_id AND r.status IN ('Reviewed', 'Kept', 'Discarded'))
        WHEN 'watch' THEN EXISTS (SELECT 1 FROM agentd.pr_watches AS w WHERE w.id = p_source_id AND w.pending IS NOT NULL
                                    AND w.pending <> 'null'::jsonb)
        ELSE false
    END
$$;

-- The threads for a person, newest activity first in three groups: waiting on them (and not marked read since agentd
-- last wrote), with unread messages, the rest. p_recipient: only threads addressed to that person (Mine).
CREATE OR REPLACE FUNCTION agentd.inbox_threads(p_user text, p_recipient text, p_limit int)
RETURNS TABLE (
    source text, source_id bigint, title text, recipient text, latest_id bigint, latest_kind text, latest_preview text,
    latest_at timestamptz, items int, unread int, waiting boolean, question_id bigint, question text, question_options jsonb)
LANGUAGE sql STABLE
AS $$
    WITH t AS (
        SELECT i.source, i.source_id, max(i.id) AS latest_id, count(*)::int AS items
          FROM agentd.inbox_items AS i
         WHERE p_recipient IS NULL OR EXISTS (SELECT 1 FROM agentd.inbox_items AS m
                                               WHERE m.source = i.source AND m.source_id = i.source_id AND m.recipient = p_recipient)
         GROUP BY i.source, i.source_id
    ), rows AS (
        SELECT t.source, t.source_id, l.title, l.recipient, t.latest_id, l.kind AS latest_kind, left(l.markdown, 300) AS latest_preview,
               l.created_at AS latest_at, t.items,
               (SELECT count(*)::int FROM agentd.inbox_items AS u
                 WHERE u.source = t.source AND u.source_id = t.source_id AND u.id > coalesce(r.read_until, 0)) AS unread,
               agentd.inbox_waiting(t.source, t.source_id) AS waiting,
               coalesce(r.read_until, 0) AS read_until
          FROM t
          JOIN agentd.inbox_items AS l ON l.id = t.latest_id
          LEFT JOIN agentd.inbox_reads AS r ON r.user_name = p_user AND r.source = t.source AND r.source_id = t.source_id
    )
    SELECT rows.source, rows.source_id, rows.title, rows.recipient, rows.latest_id, rows.latest_kind, rows.latest_preview, rows.latest_at,
           rows.items, rows.unread, rows.waiting, q.id, q.markdown, q.options
      FROM rows
      LEFT JOIN LATERAL (
          SELECT qi.id, qi.markdown, qi.options FROM agentd.inbox_items AS qi
           WHERE rows.waiting AND qi.source = rows.source AND qi.source_id = rows.source_id AND qi.kind = 'Question'
           ORDER BY qi.id DESC LIMIT 1) AS q ON true
     ORDER BY CASE WHEN rows.waiting AND rows.read_until < rows.latest_id THEN 0 WHEN rows.unread > 0 THEN 1 ELSE 2 END,
              rows.latest_id DESC
     LIMIT greatest(1, least(coalesce(p_limit, 100), 500))
$$;

-- One thread's messages, newest first; p_before pages back.
CREATE OR REPLACE FUNCTION agentd.inbox_thread(p_source text, p_source_id bigint, p_before bigint, p_limit int)
RETURNS SETOF agentd.inbox_items
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.inbox_items AS i
     WHERE i.source = p_source AND i.source_id = p_source_id AND (p_before IS NULL OR i.id < p_before)
     ORDER BY i.id DESC
     LIMIT greatest(1, least(coalesce(p_limit, 100), 500))
$$;

-- Marks a thread read up to an item; never moves back (two tabs, or a stale page).
CREATE OR REPLACE FUNCTION agentd.inbox_mark_read(p_user text, p_source text, p_source_id bigint, p_until bigint)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.inbox_reads (user_name, source, source_id, read_until)
    VALUES (p_user, p_source, p_source_id, p_until)
    ON CONFLICT (user_name, source, source_id)
    DO UPDATE SET read_until = greatest(agentd.inbox_reads.read_until, excluded.read_until)
$$;

-- The nav badge: threads waiting on the person (not marked read since agentd last wrote), and threads with unread messages.
CREATE OR REPLACE FUNCTION agentd.inbox_counts(p_user text)
RETURNS TABLE (waiting int, unread int)
LANGUAGE sql STABLE
AS $$
    SELECT count(*) FILTER (WHERE t.waiting AND t.unread > 0)::int, count(*) FILTER (WHERE t.unread > 0)::int
      FROM agentd.inbox_threads(p_user, NULL, 500) AS t
$$;

-- Retention: drops messages older than p_before, and read marks of threads with nothing left.
CREATE OR REPLACE FUNCTION agentd.inbox_delete_before(p_before timestamptz)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    v_count int;
BEGIN
    DELETE FROM agentd.inbox_items AS i WHERE i.created_at < p_before;
    GET DIAGNOSTICS v_count = ROW_COUNT;
    DELETE FROM agentd.inbox_reads AS r
     WHERE NOT EXISTS (SELECT 1 FROM agentd.inbox_items AS i WHERE i.source = r.source AND i.source_id = r.source_id);
    RETURN v_count;
END
$$;
