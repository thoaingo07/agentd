-- A work item's whole story across its jobs (Web UI work item view). Chat threads may be deleted;
-- this history lives in agentd's own tables.

-- Every job of the work item, oldest first (first run, reruns, hand-off).
CREATE OR REPLACE FUNCTION agentd.job_list_by_work_item(p_work_item int)
RETURNS SETOF agentd.jobs
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.jobs WHERE jobs.work_item_id = p_work_item ORDER BY created_at, id
$$;

-- Events of all the work item's jobs: its story, so the steps (everything but agent.*) plus each agent turn's
-- result and the usage samples, not the agent's raw output (each run's Transcript pages that). Seq order is commit
-- order (events_serialize), so keyset paging is stable: after → ascending, before → newest first (the caller reverses).
CREATE OR REPLACE FUNCTION agentd.event_list_by_work_item(p_work_item int, p_after bigint, p_before bigint, p_limit int)
RETURNS SETOF agentd.events
LANGUAGE sql STABLE
AS $$
    SELECT e.* FROM agentd.events AS e
     WHERE e.job_id IN (SELECT id FROM agentd.jobs WHERE work_item_id = p_work_item)
       AND (p_before IS NOT NULL OR e.seq > coalesce(p_after, 0))
       AND (p_before IS NULL OR e.seq < p_before)
       AND (e.type NOT LIKE 'agent.%' OR e.type IN ('agent.result', 'agent.rate_limit'))
     ORDER BY CASE WHEN p_before IS NULL THEN e.seq END ASC,
              CASE WHEN p_before IS NOT NULL THEN e.seq END DESC
     LIMIT p_limit
$$;

-- What agentd posted for the work item (every provider, with delivery status), oldest first. Heartbeats
-- (status messages that replace each other every minute) are left out.
CREATE OR REPLACE FUNCTION agentd.outbox_list_by_work_item(p_work_item int, p_limit int)
RETURNS TABLE (id bigint, job_id bigint, provider text, kind text, status text, markdown text, created_at timestamptz, last_error text)
LANGUAGE sql STABLE
AS $$
    SELECT o.id, o.job_id, o.provider, o.kind, o.status, o.payload ->> 'markdown', o.created_at, o.last_error
      FROM agentd.outbound_messages AS o
     WHERE o.job_id IN (SELECT id FROM agentd.jobs WHERE work_item_id = p_work_item)
       AND NOT coalesce((o.payload ->> 'replaceStatusMessage')::boolean, false)
     ORDER BY o.created_at, o.id
     LIMIT p_limit
$$;
