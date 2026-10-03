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
CREATE OR REPLACE FUNCTION agentd.job_search(p_states text[], p_repo text, p_q text, p_offset int, p_limit int)
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.jobs AS j
     WHERE (p_states IS NULL OR j.state = ANY (p_states))
       AND (p_repo IS NULL OR j.repo = p_repo)
       AND (p_q IS NULL OR j.title ILIKE '%' || p_q || '%' OR j.work_item_id::text = p_q)
     ORDER BY j.created_at DESC, j.id DESC
    OFFSET p_offset LIMIT p_limit
$$;

CREATE OR REPLACE FUNCTION agentd.job_search_count(p_states text[], p_repo text, p_q text)
RETURNS bigint
LANGUAGE sql STABLE
AS $$
    SELECT count(*) FROM agentd.jobs AS j
     WHERE (p_states IS NULL OR j.state = ANY (p_states))
       AND (p_repo IS NULL OR j.repo = p_repo)
       AND (p_q IS NULL OR j.title ILIKE '%' || p_q || '%' OR j.work_item_id::text = p_q)
$$;
