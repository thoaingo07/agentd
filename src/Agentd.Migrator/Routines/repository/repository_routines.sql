-- Repository registry routines. Upsert keys on the name; the remote URL is unique too (AG409 on clash).
CREATE OR REPLACE FUNCTION agentd.repository_upsert(
    p_name text, p_remote_url text, p_organization text, p_project text, p_repo text,
    p_base_branch text, p_match_tag text, p_match_area_paths text[])
RETURNS SETOF agentd.repositories
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    INSERT INTO agentd.repositories AS r
        (name, remote_url, organization, project, repo, base_branch, match_tag, match_area_paths)
    VALUES (p_name, p_remote_url, p_organization, p_project, p_repo, p_base_branch, p_match_tag, coalesce(p_match_area_paths, '{}'))
    ON CONFLICT (name) DO UPDATE
        SET remote_url = EXCLUDED.remote_url, organization = EXCLUDED.organization, project = EXCLUDED.project,
            repo = EXCLUDED.repo, base_branch = EXCLUDED.base_branch, match_tag = EXCLUDED.match_tag,
            match_area_paths = EXCLUDED.match_area_paths, enabled = true,
            updated_at = now(), version = r.version + 1
    RETURNING r.*;
EXCEPTION
    WHEN unique_violation THEN
        RAISE EXCEPTION 'another repository is already registered for %', p_remote_url USING ERRCODE = 'AG409';
END
$$;

CREATE OR REPLACE FUNCTION agentd.repository_get(p_name text)
RETURNS SETOF agentd.repositories
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.repositories WHERE repositories.name = p_name AND repositories.enabled $$;

CREATE OR REPLACE FUNCTION agentd.repository_list()
RETURNS SETOF agentd.repositories
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.repositories WHERE repositories.enabled ORDER BY repositories.name $$;

-- Soft-remove (jobs keep their history); returns whether a repository was removed.
CREATE OR REPLACE FUNCTION agentd.repository_remove(p_name text)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH removed AS (
        UPDATE agentd.repositories SET enabled = false, updated_at = now(), version = version + 1
         WHERE name = p_name AND enabled RETURNING 1)
    SELECT EXISTS (SELECT 1 FROM removed)
$$;
