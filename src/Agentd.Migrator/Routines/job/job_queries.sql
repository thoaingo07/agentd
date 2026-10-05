-- Read routines for jobs. All return full rows (SETOF agentd.jobs); callers map columns by name.
CREATE OR REPLACE FUNCTION agentd.job_get(p_id bigint)
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.jobs WHERE jobs.id = p_id $$;

CREATE OR REPLACE FUNCTION agentd.job_find_active_by_work_item(p_work_item_id int)
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.jobs
     WHERE jobs.work_item_id = p_work_item_id AND jobs.state NOT IN ('Done', 'Failed', 'Cancelled')
$$;

CREATE OR REPLACE FUNCTION agentd.job_list_by_state(p_states text[])
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.jobs WHERE jobs.state = ANY (p_states) ORDER BY jobs.created_at, jobs.id $$;

CREATE OR REPLACE FUNCTION agentd.job_list_recent(p_since timestamptz)
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.jobs WHERE jobs.created_at >= p_since ORDER BY jobs.created_at, jobs.id $$;

-- History search (newest first): optional states, repository and free text (title or work item id).
-- History search, newest first by when the job last changed (for a finished job: when it finished).
-- p_q matches the title (case-insensitive substring); p_work_item matches the work item id exactly;
-- given both, either may match. p_from/p_to bound updated_at (to is exclusive).
DROP FUNCTION IF EXISTS agentd.job_search(text[], text, text, int, int);
DROP FUNCTION IF EXISTS agentd.job_search_count(text[], text, text);

CREATE OR REPLACE FUNCTION agentd.job_search(
    p_states text[], p_repo text, p_q text, p_work_item int, p_from timestamptz, p_to timestamptz, p_offset int, p_limit int)
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.jobs AS j
     WHERE (p_states IS NULL OR j.state = ANY (p_states))
       AND (p_repo IS NULL OR j.repo = p_repo)
       AND ((p_q IS NULL AND p_work_item IS NULL)
            OR (p_q IS NOT NULL AND j.title ILIKE '%' || p_q || '%')
            OR (p_work_item IS NOT NULL AND j.work_item_id = p_work_item))
       AND (p_from IS NULL OR j.updated_at >= p_from)
       AND (p_to IS NULL OR j.updated_at < p_to)
     ORDER BY j.updated_at DESC, j.id DESC
    OFFSET p_offset LIMIT p_limit
$$;

-- The number of matches, counted up to p_cap + 1 (so a huge history doesn't cost a full count).
CREATE OR REPLACE FUNCTION agentd.job_search_count(
    p_states text[], p_repo text, p_q text, p_work_item int, p_from timestamptz, p_to timestamptz, p_cap int)
RETURNS bigint
LANGUAGE sql STABLE
AS $$
    SELECT count(*) FROM (
        SELECT 1 FROM agentd.jobs AS j
         WHERE (p_states IS NULL OR j.state = ANY (p_states))
           AND (p_repo IS NULL OR j.repo = p_repo)
           AND ((p_q IS NULL AND p_work_item IS NULL)
                OR (p_q IS NOT NULL AND j.title ILIKE '%' || p_q || '%')
                OR (p_work_item IS NOT NULL AND j.work_item_id = p_work_item))
           AND (p_from IS NULL OR j.updated_at >= p_from)
           AND (p_to IS NULL OR j.updated_at < p_to)
         LIMIT p_cap + 1) AS capped
$$;
