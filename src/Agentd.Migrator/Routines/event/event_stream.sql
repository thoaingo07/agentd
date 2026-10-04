-- Live streaming and paging over agentd.events.

-- NOTIFY on every committed event (whoever inserted it: job_save, outbox routines, event_append).
-- PostgreSQL delivers notifications only when the transaction commits, so a rolled-back event is
-- never streamed. Payload: '<seq>' (the listener loads the row).
CREATE OR REPLACE FUNCTION agentd.events_notify()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_notify('agentd_events', NEW.seq::text);
    RETURN NULL;
END
$$;

-- Events commit in seq order. Every statement inserting events first takes a transaction-scoped
-- advisory lock, so seq values are drawn (per row, after this BEFORE STATEMENT trigger) and committed
-- one writer at a time. Without it, a transaction holding seq 10 could commit after one holding 11,
-- and a reader resuming "after 11" (UI reconnect, hub replay) would never see 10.
-- Writers insert events after their row locks (job_save: UPDATE jobs, then INSERT events), so the
-- lock is held only from the event insert to commit and doesn't order against row locks.
CREATE OR REPLACE FUNCTION agentd.events_serialize()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('agentd.events'));
    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS trg_events_serialize ON agentd.events;
CREATE TRIGGER trg_events_serialize BEFORE INSERT ON agentd.events FOR EACH STATEMENT EXECUTE FUNCTION agentd.events_serialize();

DROP TRIGGER IF EXISTS trg_events_notify ON agentd.events;
CREATE TRIGGER trg_events_notify AFTER INSERT ON agentd.events FOR EACH ROW EXECUTE FUNCTION agentd.events_notify();

-- Creates the monthly partitions for the month of p_now and the next one (idempotent). Called at
-- daemon startup and daily, so inserts never fall into the default partition.
CREATE OR REPLACE FUNCTION agentd.event_ensure_partitions(p_now timestamptz)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    m date;
    v_created int := 0;
BEGIN
    FOR m IN SELECT date_trunc('month', p_now)::date UNION SELECT (date_trunc('month', p_now) + interval '1 month')::date LOOP
        IF to_regclass(format('agentd.%I', 'events_' || to_char(m, 'YYYY_MM'))) IS NULL THEN
            EXECUTE format('CREATE TABLE agentd.%I PARTITION OF agentd.events FOR VALUES FROM (%L) TO (%L)',
                           'events_' || to_char(m, 'YYYY_MM'), m, (m + interval '1 month')::date);
            v_created := v_created + 1;
        END IF;
    END LOOP;
    RETURN v_created;
END
$$;

-- The newest committed seq, 0 when empty (the dashboard snapshot's stream position).
CREATE OR REPLACE FUNCTION agentd.event_latest_seq()
RETURNS bigint
LANGUAGE sql STABLE
AS $$ SELECT coalesce(max(seq), 0) FROM agentd.events $$;

CREATE OR REPLACE FUNCTION agentd.event_get(p_seq bigint)
RETURNS SETOF agentd.events
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.events WHERE events.seq = p_seq $$;

-- Events after p_after_seq, ascending. p_job_id null = all jobs, and then only summary types
-- (everything except the agent's raw output, agent.*), as the dashboard's "all" stream.
CREATE OR REPLACE FUNCTION agentd.event_read_after(p_job_id bigint, p_after_seq bigint, p_limit int)
RETURNS SETOF agentd.events
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.events AS e
     WHERE e.seq > p_after_seq
       AND (CASE WHEN p_job_id IS NULL THEN e.type NOT LIKE 'agent.%' ELSE e.job_id = p_job_id END)
     ORDER BY e.seq
     LIMIT p_limit
$$;

-- Events of a job before p_before_seq, newest first (the caller reverses them to ascending).
CREATE OR REPLACE FUNCTION agentd.event_read_before(p_job_id bigint, p_before_seq bigint, p_limit int)
RETURNS SETOF agentd.events
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.events AS e
     WHERE e.job_id = p_job_id AND e.seq < p_before_seq
     ORDER BY e.seq DESC
     LIMIT p_limit
$$;
