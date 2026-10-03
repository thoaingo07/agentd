-- Conversations: a job's thread per messaging provider.
-- Insert raises AG409 when the external conversation is already used or the job already has an
-- open conversation on that provider.
CREATE OR REPLACE FUNCTION agentd.conversation_insert(
    p_job_id bigint, p_provider text, p_external_conversation_id text, p_external_space_id text,
    p_link text, p_opened_at timestamptz, p_events jsonb)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_id bigint;
BEGIN
    INSERT INTO agentd.conversations (job_id, provider, external_conversation_id, external_space_id, link, opened_at)
    VALUES (p_job_id, p_provider, p_external_conversation_id, p_external_space_id, p_link, p_opened_at)
    RETURNING id INTO v_id;

    INSERT INTO agentd.events (job_id, type, payload)
    SELECT p_job_id, e ->> 'type', e -> 'payload' FROM jsonb_array_elements(coalesce(p_events, '[]'::jsonb)) AS e;

    RETURN v_id;
EXCEPTION
    WHEN unique_violation THEN
        RAISE EXCEPTION 'conversation % on % already exists, or job % already has one open there',
            p_external_conversation_id, p_provider, p_job_id USING ERRCODE = 'AG409';
    WHEN foreign_key_violation THEN
        RAISE EXCEPTION 'job % not found', p_job_id USING ERRCODE = 'AG404';
END
$$;

-- Updates the mutable parts (status message, closing). AG404 if it doesn't exist.
CREATE OR REPLACE FUNCTION agentd.conversation_update(p_id bigint, p_status_message_id text, p_closed_at timestamptz)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE agentd.conversations SET status_message_id = p_status_message_id, closed_at = p_closed_at WHERE id = p_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'conversation % not found', p_id USING ERRCODE = 'AG404';
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION agentd.conversation_list_by_job(p_job_id bigint)
RETURNS SETOF agentd.conversations
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.conversations WHERE conversations.job_id = p_job_id ORDER BY conversations.opened_at, conversations.id $$;

-- Open conversations on one provider (a polling provider watches these threads).
CREATE OR REPLACE FUNCTION agentd.conversation_list_open(p_provider text)
RETURNS SETOF agentd.conversations
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.conversations
     WHERE conversations.provider = p_provider AND conversations.closed_at IS NULL
     ORDER BY conversations.id
$$;

-- Open conversations of any job for a work item (a work item keeps one thread per provider across reruns).
CREATE OR REPLACE FUNCTION agentd.conversation_list_open_by_work_item(p_work_item_id int)
RETURNS SETOF agentd.conversations
LANGUAGE sql STABLE
AS $$
    SELECT c.* FROM agentd.conversations AS c
      JOIN agentd.jobs AS j ON j.id = c.job_id
     WHERE j.work_item_id = p_work_item_id AND c.closed_at IS NULL
     ORDER BY c.id
$$;

-- Hands a conversation to a new job of the same work item. AG409 if that job already has one open on the provider.
CREATE OR REPLACE FUNCTION agentd.conversation_move(p_id bigint, p_job_id bigint)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE agentd.conversations SET job_id = p_job_id WHERE id = p_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'conversation % not found', p_id USING ERRCODE = 'AG404';
    END IF;
EXCEPTION
    WHEN unique_violation THEN
        RAISE EXCEPTION 'job % already has an open conversation on that provider', p_job_id USING ERRCODE = 'AG409';
END
$$;

-- Routes an inbound message: which conversation (and so which job) a provider thread belongs to.
CREATE OR REPLACE FUNCTION agentd.conversation_find_external(p_provider text, p_external_conversation_id text)
RETURNS SETOF agentd.conversations
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.conversations
     WHERE conversations.provider = p_provider AND conversations.external_conversation_id = p_external_conversation_id
$$;
