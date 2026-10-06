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
-- An 'idea.updated' event (ids and status only) lets the Web UI refresh the idea live.
CREATE OR REPLACE FUNCTION agentd.idea_update(
    p_id bigint, p_status text, p_session_id uuid, p_model text, p_effort text, p_worktree_path text, p_drafts jsonb, p_created_work_items int[])
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.ideas
       SET status = p_status, session_id = p_session_id, model = p_model, effort = p_effort, worktree_path = p_worktree_path,
           drafts = p_drafts, created_work_items = p_created_work_items, updated_at = now()
     WHERE id = p_id;
    INSERT INTO agentd.events (job_id, type, payload) VALUES (NULL, 'idea.updated', jsonb_build_object('ideaId', p_id, 'status', p_status));
$$;

-- The event carries no text (the conversation stays in idea_messages); the Web UI re-reads the idea.
CREATE OR REPLACE FUNCTION agentd.idea_message_add(p_idea_id bigint, p_direction text, p_author text, p_text text)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.idea_messages (idea_id, direction, author, text) VALUES (p_idea_id, p_direction, p_author, p_text);
    INSERT INTO agentd.events (job_id, type, payload) VALUES (NULL, 'idea.message', jsonb_build_object('ideaId', p_idea_id, 'direction', p_direction));
$$;

CREATE OR REPLACE FUNCTION agentd.idea_message_list(p_idea_id bigint)
RETURNS SETOF agentd.idea_messages
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.idea_messages WHERE idea_id = p_idea_id ORDER BY id $$;

-- Ideas as the Web UI lists them, newest first: one idea (p_id), the ideas that created a work item
-- (p_work_item), or all of them (both null). Counts instead of the conversation and the drafts.
CREATE OR REPLACE FUNCTION agentd.idea_summaries(p_id bigint, p_work_item int, p_limit int)
RETURNS TABLE (id bigint, repo text, title text, author text, status text, model text, effort text, drafts integer,
               created_work_items int[], messages integer, created_at timestamptz, updated_at timestamptz)
LANGUAGE sql STABLE
AS $$
    SELECT i.id, i.repo, i.title, i.author, i.status, i.model, i.effort,
           coalesce(jsonb_array_length(i.drafts), 0), i.created_work_items,
           (SELECT count(*)::integer FROM agentd.idea_messages AS m WHERE m.idea_id = i.id), i.created_at, i.updated_at
      FROM agentd.ideas AS i
     WHERE (p_id IS NULL OR i.id = p_id) AND (p_work_item IS NULL OR p_work_item = ANY (i.created_work_items))
     ORDER BY i.id DESC
     LIMIT p_limit
$$;

-- The idea threads chat providers should read: every idea that isn't closed (a finished idea still answers
-- its close-out question and points new messages to !idea).
CREATE OR REPLACE FUNCTION agentd.idea_list_open_threads(p_provider text)
RETURNS SETOF text
LANGUAGE sql STABLE
AS $$ SELECT i.thread_id FROM agentd.ideas AS i WHERE i.provider = p_provider AND i.status <> 'Closed' ORDER BY i.id $$;
