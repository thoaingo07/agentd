-- Artifacts (HTML pages agents publish).

DROP FUNCTION IF EXISTS agentd.artifact_publish(text, text, text, bigint, bigint, text);
DROP FUNCTION IF EXISTS agentd.artifact_publish(text, text, text, text, bigint, text);
DROP FUNCTION IF EXISTS agentd.artifact_expire(timestamptz);
DROP FUNCTION IF EXISTS agentd.artifact_list(int);

-- Publishes a page: a new one gets p_name; the same source (p_source: job, chat, idea, review, review-session, watch,
-- web, with its id) publishing the same title again replaces the HTML and
-- keeps the first name (its link), and brings an expired page back. Parallel publishes of one title make one row. A job's page is announced on its
-- event stream (name and title only).
-- p_storage: 'database' (p_html is the page), 's3' (in S3-compatible storage under the name) or 'local' (an HTML file in
-- Agentd:Artifacts:Folder); p_html is null for both.
CREATE OR REPLACE FUNCTION agentd.artifact_publish(p_name text, p_title text, p_html text, p_source text, p_source_id bigint, p_author text,
                                                   p_storage text DEFAULT 'database')
RETURNS TABLE (name text, created boolean)
LANGUAGE plpgsql
AS $$
DECLARE
    v_name    text;
    v_created boolean;
BEGIN
    INSERT INTO agentd.artifacts AS a (name, title, html, source, source_id, author, storage)
    VALUES (p_name, p_title, p_html, p_source, p_source_id, p_author, p_storage)
    ON CONFLICT (source, source_id, title) DO UPDATE
        SET html = excluded.html, author = excluded.author, storage = excluded.storage, updated_at = now(), expired_at = NULL
    RETURNING a.name, (a.xmax = 0) INTO v_name, v_created;

    IF p_source = 'job' THEN
        INSERT INTO agentd.events (job_id, type, payload)
        VALUES (p_source_id, 'artifact.published', jsonb_build_object('name', v_name, 'title', p_title, 'created', v_created));
    END IF;

    RETURN QUERY SELECT v_name, v_created;
END
$$;

CREATE OR REPLACE FUNCTION agentd.artifact_get(p_name text)
RETURNS SETOF agentd.artifacts
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.artifacts WHERE artifacts.name = p_name $$;

-- The newest live pages first, without their HTML (the Web UI's list), and where each is stored (its link); expired ones aren't listed.
CREATE OR REPLACE FUNCTION agentd.artifact_list(p_limit int)
RETURNS TABLE (name text, title text, source text, source_id bigint, author text, created_at timestamptz, updated_at timestamptz, storage text)
LANGUAGE sql STABLE
AS $$
    SELECT a.name, a.title, a.source, a.source_id, a.author, a.created_at, a.updated_at, a.storage
      FROM agentd.artifacts AS a
     WHERE a.expired_at IS NULL
     ORDER BY a.updated_at DESC, a.id DESC
     LIMIT p_limit
$$;

-- Expires pages last updated before p_before: drops the HTML and keeps a tombstone (the link answers "expired").
-- Returns the pages that expired now and where they were stored (S3 objects are then deleted); parallel callers never
-- return a page twice.
CREATE OR REPLACE FUNCTION agentd.artifact_expire(p_before timestamptz)
RETURNS TABLE (name text, storage text)
LANGUAGE sql
AS $$
    UPDATE agentd.artifacts AS a SET html = NULL, expired_at = now()
     WHERE a.expired_at IS NULL AND a.updated_at < p_before
    RETURNING a.name, a.storage
$$;
