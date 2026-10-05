-- Brainstormed ideas and their conversation.

CREATE OR REPLACE FUNCTION agentd.idea_insert(p_repo text, p_title text, p_author text, p_provider text, p_thread_id text, p_space_id text)
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.ideas (repo, title, author, provider, thread_id, space_id)
    VALUES (p_repo, p_title, p_author, p_provider, p_thread_id, p_space_id)
    RETURNING id
$$;

CREATE OR REPLACE FUNCTION agentd.idea_get(p_id bigint)
RETURNS SETOF agentd.ideas
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.ideas WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.idea_find_by_thread(p_provider text, p_thread_id text)
RETURNS SETOF agentd.ideas
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.ideas WHERE provider = p_provider AND thread_id = p_thread_id $$;

-- Saves the mutable fields (status, session, model and effort, checkout, drafts, created work items).
CREATE OR REPLACE FUNCTION agentd.idea_update(
    p_id bigint, p_status text, p_session_id uuid, p_model text, p_effort text, p_worktree_path text, p_drafts jsonb, p_created_work_items int[])
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.ideas
       SET status = p_status, session_id = p_session_id, model = p_model, effort = p_effort, worktree_path = p_worktree_path,
           drafts = p_drafts, created_work_items = p_created_work_items, updated_at = now()
     WHERE id = p_id
$$;

CREATE OR REPLACE FUNCTION agentd.idea_message_add(p_idea_id bigint, p_direction text, p_author text, p_text text)
RETURNS void
LANGUAGE sql
AS $$ INSERT INTO agentd.idea_messages (idea_id, direction, author, text) VALUES (p_idea_id, p_direction, p_author, p_text) $$;

CREATE OR REPLACE FUNCTION agentd.idea_message_list(p_idea_id bigint)
RETURNS SETOF agentd.idea_messages
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.idea_messages WHERE idea_id = p_idea_id ORDER BY id $$;
