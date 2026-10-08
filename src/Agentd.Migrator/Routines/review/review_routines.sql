-- PR reviews in chat and their conversation. Events carry ids and status only (never the text), so the Web UI can
-- refresh live.

CREATE OR REPLACE FUNCTION agentd.pr_review_insert(p_repo text, p_pull_request_id int, p_title text, p_author text, p_provider text, p_thread_id text, p_space_id text)
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.pr_reviews (repo, pull_request_id, title, author, provider, thread_id, space_id)
    VALUES (p_repo, p_pull_request_id, p_title, p_author, p_provider, p_thread_id, p_space_id)
    RETURNING id
$$;

CREATE OR REPLACE FUNCTION agentd.pr_review_get(p_id bigint)
RETURNS SETOF agentd.pr_reviews
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_reviews WHERE id = p_id $$;

-- The latest review of a PR (newest first): `!review` on the same PR continues it.
CREATE OR REPLACE FUNCTION agentd.pr_review_find_latest(p_repo text, p_pull_request_id int)
RETURNS SETOF agentd.pr_reviews
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_reviews WHERE repo = p_repo AND pull_request_id = p_pull_request_id ORDER BY id DESC LIMIT 1 $$;

CREATE OR REPLACE FUNCTION agentd.pr_review_find_by_thread(p_provider text, p_thread_id text)
RETURNS SETOF agentd.pr_reviews
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_reviews WHERE provider = p_provider AND thread_id = p_thread_id $$;

CREATE OR REPLACE FUNCTION agentd.pr_review_update(
    p_id bigint, p_status text, p_head_commit text, p_focus text, p_session_id uuid, p_model text, p_effort text,
    p_worktree_path text, p_result jsonb, p_posted_threads int[])
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.pr_reviews
       SET status = p_status, head_commit = p_head_commit, focus = p_focus, session_id = p_session_id, model = p_model,
           effort = p_effort, worktree_path = p_worktree_path, result = p_result, posted_threads = p_posted_threads, updated_at = now()
     WHERE id = p_id;
    INSERT INTO agentd.events (job_id, type, payload) VALUES (NULL, 'review.updated', jsonb_build_object('reviewId', p_id, 'status', p_status));
$$;

CREATE OR REPLACE FUNCTION agentd.pr_review_message_add(p_review_id bigint, p_direction text, p_author text, p_text text)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.pr_review_messages (review_id, direction, author, text) VALUES (p_review_id, p_direction, p_author, p_text);
    INSERT INTO agentd.events (job_id, type, payload) VALUES (NULL, 'review.message', jsonb_build_object('reviewId', p_review_id, 'direction', p_direction));
$$;

CREATE OR REPLACE FUNCTION agentd.pr_review_message_list(p_review_id bigint)
RETURNS SETOF agentd.pr_review_messages
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_review_messages WHERE review_id = p_review_id ORDER BY id $$;

-- The review threads chat providers should read: every review that isn't closed.
-- Posted reviews, which agentd keeps re-checking after pushes until their PR is completed or abandoned.
CREATE OR REPLACE FUNCTION agentd.pr_review_list_posted()
RETURNS SETOF agentd.pr_reviews
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.pr_reviews WHERE status = 'Posted' ORDER BY id $$;

CREATE OR REPLACE FUNCTION agentd.pr_review_list_open_threads(p_provider text)
RETURNS SETOF text
LANGUAGE sql STABLE
AS $$ SELECT r.thread_id FROM agentd.pr_reviews AS r WHERE r.provider = p_provider AND r.status <> 'Closed' ORDER BY r.id $$;
