-- 202610070001 events: partitioned by month on ts (for paging, live streaming and later retention).
-- The table is rebuilt: rows keep their seq, and the identity continues after the highest one.
ALTER TABLE agentd.events RENAME TO events_unpartitioned;
ALTER INDEX agentd.ix_events_job_seq RENAME TO ix_events_unpartitioned_job_seq;

CREATE TABLE agentd.events (
    seq      bigint GENERATED ALWAYS AS IDENTITY,
    job_id   bigint      NULL REFERENCES agentd.jobs (id),
    ts       timestamptz NOT NULL DEFAULT now(),
    type     text        NOT NULL,
    payload  jsonb       NOT NULL,
    PRIMARY KEY (seq, ts)          -- the partition key must be part of the primary key
) PARTITION BY RANGE (ts);

CREATE INDEX ix_events_job_seq ON agentd.events (job_id, seq);
CREATE INDEX ix_events_seq ON agentd.events (seq);

-- Safety net: rows outside every monthly partition land here (the daemon creates months ahead of time).
CREATE TABLE agentd.events_default PARTITION OF agentd.events DEFAULT;

-- One partition per month present in the old data, plus this month and the next.
DO $$
DECLARE
    m date;
BEGIN
    FOR m IN
        SELECT DISTINCT date_trunc('month', ts)::date FROM agentd.events_unpartitioned
        UNION SELECT date_trunc('month', now())::date
        UNION SELECT (date_trunc('month', now()) + interval '1 month')::date
    LOOP
        EXECUTE format('CREATE TABLE IF NOT EXISTS agentd.%I PARTITION OF agentd.events FOR VALUES FROM (%L) TO (%L)',
                       'events_' || to_char(m, 'YYYY_MM'), m, (m + interval '1 month')::date);
    END LOOP;
END
$$;

INSERT INTO agentd.events (seq, job_id, ts, type, payload) OVERRIDING SYSTEM VALUE
SELECT seq, job_id, ts, type, payload FROM agentd.events_unpartitioned;

SELECT setval(pg_get_serial_sequence('agentd.events', 'seq'),
              greatest((SELECT coalesce(max(seq), 0) FROM agentd.events), 1),
              (SELECT count(*) > 0 FROM agentd.events));

DROP TABLE agentd.events_unpartitioned;
