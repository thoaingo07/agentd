-- Artifacts (HTML pages agents publish).

-- Publishes a page: a new one gets p_name; the same job or chat publishing the same title again replaces the HTML and
-- keeps the first name (its link). Parallel publishes of one title make one row. A job's page is announced on its
-- event stream (name and title only).
CREATE OR REPLACE FUNCTION agentd.artifact_publish(p_name text, p_title text, p_html text, p_job_id bigint, p_chat_id bigint, p_author text)
RETURNS TABLE (name text, created boolean)
LANGUAGE plpgsql
AS $$
DECLARE
    v_name    text;
    v_created boolean;
BEGIN
    INSERT INTO agentd.artifacts AS a (name, title, html, job_id, chat_id, author)
    VALUES (p_name, p_title, p_html, p_job_id, p_chat_id, p_author)
    ON CONFLICT (job_id, chat_id, title) DO UPDATE SET html = excluded.html, author = excluded.author, updated_at = now()
    RETURNING a.name, (a.xmax = 0) INTO v_name, v_created;

    IF p_job_id IS NOT NULL THEN
        INSERT INTO agentd.events (job_id, type, payload)
        VALUES (p_job_id, 'artifact.published', jsonb_build_object('name', v_name, 'title', p_title, 'created', v_created));
    END IF;

    RETURN QUERY SELECT v_name, v_created;
END
$$;

CREATE OR REPLACE FUNCTION agentd.artifact_get(p_name text)
RETURNS SETOF agentd.artifacts
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.artifacts WHERE artifacts.name = p_name $$;

-- The newest pages first, without their HTML (the Web UI's list).
CREATE OR REPLACE FUNCTION agentd.artifact_list(p_limit int)
RETURNS TABLE (name text, title text, job_id bigint, chat_id bigint, author text, created_at timestamptz, updated_at timestamptz)
LANGUAGE sql STABLE
AS $$
    SELECT a.name, a.title, a.job_id, a.chat_id, a.author, a.created_at, a.updated_at
      FROM agentd.artifacts AS a
     ORDER BY a.updated_at DESC, a.id DESC
     LIMIT p_limit
$$;
