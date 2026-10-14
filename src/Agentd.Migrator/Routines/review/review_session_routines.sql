-- Review sessions (docs/architect/review-sessions.md) and their comments and questions.

CREATE OR REPLACE FUNCTION agentd.review_session_insert(p_repo text, p_target text, p_pull_request_id int, p_head_ref text, p_base_ref text,
                                                        p_created_by text, p_model text, p_effort text)
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.review_sessions (repo, target, pull_request_id, head_ref, base_ref, created_by, model, effort)
    VALUES (p_repo, p_target, p_pull_request_id, p_head_ref, p_base_ref, p_created_by, p_model, p_effort)
    RETURNING id
$$;

-- The focus and instructions a review was started with (both optional).
CREATE OR REPLACE FUNCTION agentd.review_session_set_brief(p_id bigint, p_focus text, p_instructions text)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_sessions SET focus = p_focus, instructions = p_instructions, updated_at = now() WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.review_session_get(p_id bigint)
RETURNS SETOF agentd.review_sessions
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_sessions WHERE id = p_id $$;

-- Someone's recent sessions and every review started in chat (the team sees those in its channel anyway), newest first.
CREATE OR REPLACE FUNCTION agentd.review_session_list(p_created_by text, p_limit int)
RETURNS SETOF agentd.review_sessions
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.review_sessions WHERE created_by = p_created_by OR chat_link IS NOT NULL ORDER BY id DESC LIMIT p_limit
$$;

-- A session started from chat: the conversation's link, and for a PR's !review the chat review whose findings fill it.
CREATE OR REPLACE FUNCTION agentd.review_session_link_chat(p_id bigint, p_chat_link text, p_pr_review_id bigint)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_sessions SET chat_link = p_chat_link, pr_review_id = p_pr_review_id, updated_at = now() WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.review_session_find_by_pr_review(p_pr_review_id bigint)
RETURNS SETOF agentd.review_sessions
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_sessions WHERE pr_review_id = p_pr_review_id $$;

-- The chat review's latest findings replace the session's (one atomic update); a finding with the same file and title
-- keeps the decision made on the page. Returns how many there are now.
CREATE OR REPLACE FUNCTION agentd.review_session_set_findings(p_id bigint, p_findings jsonb, p_summary text)
RETURNS int
LANGUAGE sql
AS $$
    UPDATE agentd.review_sessions s
       SET findings = COALESCE((
               SELECT jsonb_agg(n.f || COALESCE((
                          SELECT jsonb_build_object('decision', o.f -> 'decision', 'edited', o.f -> 'edited')
                            FROM jsonb_array_elements(s.findings) AS o(f)
                           WHERE o.f ->> 'title' = n.f ->> 'title' AND o.f ->> 'file' IS NOT DISTINCT FROM n.f ->> 'file'
                           LIMIT 1), '{}'::jsonb) ORDER BY n.i)
                 FROM jsonb_array_elements(p_findings) WITH ORDINALITY AS n(f, i)), '[]'::jsonb),
           summary = COALESCE(p_summary, summary),
           updated_at = now()
     WHERE s.id = p_id
    RETURNING jsonb_array_length(findings)
$$;

-- Sessions in one status (e.g. the reviews to resume after a restart), oldest first.
CREATE OR REPLACE FUNCTION agentd.review_session_list_by_status(p_status text)
RETURNS SETOF agentd.review_sessions
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_sessions WHERE status = p_status ORDER BY id $$;

CREATE OR REPLACE FUNCTION agentd.review_session_pin(p_id bigint, p_base_commit text, p_head_commit text, p_worktree_path text)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.review_sessions
       SET base_commit = p_base_commit, head_commit = p_head_commit, worktree_path = p_worktree_path, updated_at = now()
     WHERE id = p_id
$$;

CREATE OR REPLACE FUNCTION agentd.review_session_set_status(p_id bigint, p_status text, p_error text, p_sent_to text)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.review_sessions
       SET status = p_status, error = p_error, sent_to = COALESCE(p_sent_to, sent_to), updated_at = now()
     WHERE id = p_id
$$;

-- Findings stream in while the reviewer works: appended, never replaced, so nothing a reader saw disappears.
CREATE OR REPLACE FUNCTION agentd.review_session_add_findings(p_id bigint, p_findings jsonb, p_summary text)
RETURNS int
LANGUAGE sql
AS $$
    UPDATE agentd.review_sessions
       SET findings = findings || p_findings, summary = COALESCE(p_summary, summary), updated_at = now()
     WHERE id = p_id
    RETURNING jsonb_array_length(findings)
$$;

-- The reviewer's guide (a guided review), replaced whole: a re-review writes a new one.
CREATE OR REPLACE FUNCTION agentd.review_session_set_guide(p_id bigint, p_guide jsonb)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_sessions SET guide = p_guide, updated_at = now() WHERE id = p_id $$;

-- Keep / drop / edit one finding (0-based). One atomic update per call, so decisions on different findings from
-- parallel requests never overwrite each other. False when there's no such finding.
CREATE OR REPLACE FUNCTION agentd.review_session_decide(p_id bigint, p_index int, p_decision text, p_edited text)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH updated AS (
        UPDATE agentd.review_sessions
           SET findings = jsonb_set(findings, ARRAY[p_index::text],
                   findings -> p_index || jsonb_build_object('decision', p_decision, 'edited', p_edited)),
               updated_at = now()
         WHERE id = p_id AND p_index >= 0 AND p_index < jsonb_array_length(findings)
        RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM updated)
$$;

CREATE OR REPLACE FUNCTION agentd.review_comment_add(p_session_id bigint, p_file text, p_line int, p_end_line int, p_text text, p_author text)
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.review_comments (session_id, file, line, end_line, text, author)
    VALUES (p_session_id, p_file, p_line, p_end_line, p_text, p_author)
    RETURNING id
$$;

-- Only its author removes a comment.
CREATE OR REPLACE FUNCTION agentd.review_comment_delete(p_session_id bigint, p_id bigint, p_author text)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH deleted AS (
        DELETE FROM agentd.review_comments WHERE session_id = p_session_id AND id = p_id AND author = p_author RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM deleted)
$$;

-- Only its author rewords a comment (a draft until the review is sent).
CREATE OR REPLACE FUNCTION agentd.review_comment_update(p_session_id bigint, p_id bigint, p_author text, p_text text)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH updated AS (
        UPDATE agentd.review_comments SET text = p_text WHERE session_id = p_session_id AND id = p_id AND author = p_author RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM updated)
$$;

CREATE OR REPLACE FUNCTION agentd.review_comment_list(p_session_id bigint)
RETURNS SETOF agentd.review_comments
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_comments WHERE session_id = p_session_id ORDER BY id $$;

DROP FUNCTION IF EXISTS agentd.review_ask_add(bigint, text, int, int, text, text);

-- A new question starts a thread (it gets the agent session its follow-ups resume). With p_thread_id it's a follow-up
-- at the thread's place: 0 when there's no such thread in the session, -1 while the thread's last question still waits
-- for its answer (the thread's first question is locked, so parallel follow-ups add one and refuse the others).
CREATE OR REPLACE FUNCTION agentd.review_ask_add(p_session_id bigint, p_file text, p_line int, p_end_line int, p_question text, p_author text,
                                                 p_thread_id bigint DEFAULT NULL)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_root agentd.review_asks;
    v_id   bigint;
BEGIN
    IF p_thread_id IS NULL THEN
        INSERT INTO agentd.review_asks (session_id, file, line, end_line, question, author, agent_session)
        VALUES (p_session_id, p_file, p_line, p_end_line, p_question, p_author, gen_random_uuid())
        RETURNING id INTO v_id;
        RETURN v_id;
    END IF;

    SELECT * INTO v_root FROM agentd.review_asks
    WHERE id = p_thread_id AND session_id = p_session_id AND thread_id IS NULL
    FOR UPDATE;
    IF NOT FOUND THEN
        RETURN 0;
    END IF;

    IF EXISTS (SELECT 1 FROM agentd.review_asks WHERE (id = p_thread_id OR thread_id = p_thread_id) AND answer IS NULL) THEN
        RETURN -1;
    END IF;

    INSERT INTO agentd.review_asks (session_id, file, line, end_line, question, author, thread_id)
    VALUES (p_session_id, v_root.file, v_root.line, v_root.end_line, p_question, p_author, p_thread_id)
    RETURNING id INTO v_id;
    RETURN v_id;
END
$$;

-- The thread's agent session started over (its old one couldn't be resumed).
CREATE OR REPLACE FUNCTION agentd.review_ask_set_session(p_thread_id bigint, p_session uuid)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_asks SET agent_session = p_session WHERE id = p_thread_id AND thread_id IS NULL $$;

CREATE OR REPLACE FUNCTION agentd.review_ask_answer(p_id bigint, p_answer text)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_asks SET answer = p_answer, answered_at = now() WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.review_ask_list(p_session_id bigint)
RETURNS SETOF agentd.review_asks
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_asks WHERE session_id = p_session_id ORDER BY id $$;

-- A Send to a PR (review-sessions.md §4): what was posted, kept for the re-checks after pushes. Sending again replaces it.
CREATE OR REPLACE FUNCTION agentd.review_session_post_set(p_session_id bigint, p_pull_request_id int, p_posted jsonb, p_head text)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.review_session_posts (session_id, pull_request_id, posted, head)
    VALUES (p_session_id, p_pull_request_id, p_posted, p_head)
    ON CONFLICT (session_id) DO UPDATE
       SET pull_request_id = excluded.pull_request_id, posted = excluded.posted, head = excluded.head,
           rechecks = '[]'::jsonb, done = false, updated_at = now()
$$;

CREATE OR REPLACE FUNCTION agentd.review_session_post_get(p_session_id bigint)
RETURNS SETOF agentd.review_session_posts
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_session_posts WHERE session_id = p_session_id $$;

-- The Sends still re-checked (their PR is open), oldest first.
CREATE OR REPLACE FUNCTION agentd.review_session_post_list_open()
RETURNS SETOF agentd.review_session_posts
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_session_posts WHERE NOT done ORDER BY session_id $$;

-- After a re-check (or the replies seen moving on): the findings' new statuses and head; p_recheck, when given, is added to
-- the history ({ at, head, fixed, open, closed, added }). One statement, so parallel updates never lose a history entry.
CREATE OR REPLACE FUNCTION agentd.review_session_post_update(p_session_id bigint, p_posted jsonb, p_head text, p_recheck jsonb)
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.review_session_posts
       SET posted = p_posted, head = p_head, updated_at = now(),
           rechecks = CASE WHEN p_recheck IS NULL THEN rechecks ELSE rechecks || jsonb_build_array(p_recheck) END
     WHERE session_id = p_session_id AND NOT done
$$;

-- The PR completed or was abandoned: no more re-checks.
CREATE OR REPLACE FUNCTION agentd.review_session_post_done(p_session_id bigint)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_session_posts SET done = true, updated_at = now() WHERE session_id = p_session_id $$;
