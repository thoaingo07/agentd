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

CREATE OR REPLACE FUNCTION agentd.review_session_get(p_id bigint)
RETURNS SETOF agentd.review_sessions
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_sessions WHERE id = p_id $$;

-- Someone's recent sessions, newest first.
CREATE OR REPLACE FUNCTION agentd.review_session_list(p_created_by text, p_limit int)
RETURNS SETOF agentd.review_sessions
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_sessions WHERE created_by = p_created_by ORDER BY id DESC LIMIT p_limit $$;

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

CREATE OR REPLACE FUNCTION agentd.review_comment_list(p_session_id bigint)
RETURNS SETOF agentd.review_comments
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_comments WHERE session_id = p_session_id ORDER BY id $$;

CREATE OR REPLACE FUNCTION agentd.review_ask_add(p_session_id bigint, p_file text, p_line int, p_end_line int, p_question text, p_author text)
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.review_asks (session_id, file, line, end_line, question, author)
    VALUES (p_session_id, p_file, p_line, p_end_line, p_question, p_author)
    RETURNING id
$$;

CREATE OR REPLACE FUNCTION agentd.review_ask_answer(p_id bigint, p_answer text)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.review_asks SET answer = p_answer, answered_at = now() WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.review_ask_list(p_session_id bigint)
RETURNS SETOF agentd.review_asks
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.review_asks WHERE session_id = p_session_id ORDER BY id $$;
