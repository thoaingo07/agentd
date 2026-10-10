-- The PR Monitor's watch list (docs/architect/pr-reviewer-and-monitor.md §2.0).

-- One active watch per PR: parallel callers all get the same id, and only one of them created it (created = true).
CREATE OR REPLACE FUNCTION agentd.pr_watch_insert(p_repo text, p_pull_request_id int, p_title text, p_watched_by text, p_provider text,
                                                  p_thread_id text, p_space_id text, p_seen_comments int[])
RETURNS TABLE (id bigint, created boolean)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
        INSERT INTO agentd.pr_watches AS w (repo, pull_request_id, title, watched_by, provider, thread_id, space_id, seen_comments)
        VALUES (p_repo, p_pull_request_id, p_title, p_watched_by, p_provider, p_thread_id, p_space_id, p_seen_comments)
        ON CONFLICT (repo, pull_request_id) WHERE status = 'Watching' DO NOTHING
        RETURNING w.id, true;
    IF NOT FOUND THEN
        RETURN QUERY SELECT w.id, false FROM agentd.pr_watches w WHERE w.repo = p_repo AND w.pull_request_id = p_pull_request_id AND w.status = 'Watching';
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION agentd.pr_watch_get(p_id bigint)
RETURNS SETOF agentd.pr_watches
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_watches WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.pr_watch_find_active(p_repo text, p_pull_request_id int)
RETURNS SETOF agentd.pr_watches
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_watches WHERE repo = p_repo AND pull_request_id = p_pull_request_id AND status = 'Watching' $$;

CREATE OR REPLACE FUNCTION agentd.pr_watch_find_by_thread(p_provider text, p_thread_id text)
RETURNS SETOF agentd.pr_watches
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_watches WHERE provider = p_provider AND thread_id = p_thread_id $$;

CREATE OR REPLACE FUNCTION agentd.pr_watch_list_active()
RETURNS SETOF agentd.pr_watches
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_watches WHERE status = 'Watching' ORDER BY id $$;

CREATE OR REPLACE FUNCTION agentd.pr_watch_stop(p_id bigint)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH stopped AS (
        UPDATE agentd.pr_watches SET status = 'Stopped', pending = NULL, updated_at = now() WHERE id = p_id AND status = 'Watching' RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM stopped)
$$;

-- The monitor's bookkeeping, written whole (one writer: the monitor's pass for this PR).
CREATE OR REPLACE FUNCTION agentd.pr_watch_update(p_id bigint, p_fix_rounds int, p_seen_comments int[], p_last_build_id int, p_signal_at timestamptz, p_pending jsonb)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.pr_watches
       SET fix_rounds = p_fix_rounds, seen_comments = p_seen_comments, last_build_id = p_last_build_id, signal_at = p_signal_at,
           pending = p_pending, updated_at = now()
     WHERE id = p_id
$$;

-- A notice the monitor wrote about the watch (also posted to its 👀 thread, if it has one). The event carries ids only.
CREATE OR REPLACE FUNCTION agentd.pr_watch_message_add(p_watch_id bigint, p_text text)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.pr_watch_messages (watch_id, text) VALUES (p_watch_id, p_text);
    INSERT INTO agentd.events (job_id, type, payload) VALUES (NULL, 'watch.message', jsonb_build_object('watchId', p_watch_id));
$$;

CREATE OR REPLACE FUNCTION agentd.pr_watch_message_list(p_watch_id bigint)
RETURNS SETOF agentd.pr_watch_messages
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_watch_messages WHERE watch_id = p_watch_id ORDER BY id $$;
