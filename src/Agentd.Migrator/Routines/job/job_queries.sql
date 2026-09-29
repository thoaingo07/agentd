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
