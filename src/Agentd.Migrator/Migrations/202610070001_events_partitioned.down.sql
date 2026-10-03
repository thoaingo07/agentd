-- Back to one table (data kept).
ALTER TABLE agentd.events RENAME TO events_partitioned;
CREATE TABLE agentd.events (
    seq      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id   bigint      NULL REFERENCES agentd.jobs (id),
    ts       timestamptz NOT NULL DEFAULT now(),
    type     text        NOT NULL,
    payload  jsonb       NOT NULL
);
INSERT INTO agentd.events (seq, job_id, ts, type, payload) OVERRIDING SYSTEM VALUE
SELECT seq, job_id, ts, type, payload FROM agentd.events_partitioned;
SELECT setval(pg_get_serial_sequence('agentd.events', 'seq'), greatest((SELECT coalesce(max(seq), 0) FROM agentd.events), 1));
DROP TABLE agentd.events_partitioned CASCADE;
CREATE INDEX ix_events_job_seq ON agentd.events (job_id, seq);
