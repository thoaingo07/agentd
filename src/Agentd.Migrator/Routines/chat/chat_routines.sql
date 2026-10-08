-- Chats (!chat) and their conversation.

CREATE OR REPLACE FUNCTION agentd.chat_insert(p_author text, p_provider text, p_thread_id text, p_space_id text, p_repos text[])
RETURNS bigint
LANGUAGE sql
AS $$
    INSERT INTO agentd.chats (author, provider, thread_id, space_id, repos) VALUES (p_author, p_provider, p_thread_id, p_space_id, p_repos)
    RETURNING id
$$;

CREATE OR REPLACE FUNCTION agentd.chat_get(p_id bigint)
RETURNS SETOF agentd.chats
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.chats WHERE id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.chat_find_by_thread(p_provider text, p_thread_id text)
RETURNS SETOF agentd.chats
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.chats WHERE provider = p_provider AND thread_id = p_thread_id $$;

CREATE OR REPLACE FUNCTION agentd.chat_update(p_id bigint, p_status text, p_session_id uuid, p_model text, p_effort text, p_worktrees text[])
RETURNS void
LANGUAGE sql
AS $$
    UPDATE agentd.chats
       SET status = p_status, session_id = p_session_id, model = p_model, effort = p_effort, worktrees = p_worktrees, updated_at = now()
     WHERE id = p_id
$$;

CREATE OR REPLACE FUNCTION agentd.chat_message_add(p_chat_id bigint, p_direction text, p_author text, p_text text)
RETURNS void
LANGUAGE sql
AS $$ INSERT INTO agentd.chat_messages (chat_id, direction, author, text) VALUES (p_chat_id, p_direction, p_author, p_text) $$;

CREATE OR REPLACE FUNCTION agentd.chat_open_threads(p_provider text)
RETURNS SETOF text
LANGUAGE sql STABLE
AS $$ SELECT thread_id FROM agentd.chats WHERE provider = p_provider AND status = 'Open' ORDER BY id $$;
