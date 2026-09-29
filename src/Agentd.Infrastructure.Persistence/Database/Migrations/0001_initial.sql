-- 0001_initial: core tables. Versioned migrations are immutable once applied.
CREATE TABLE agentd.jobs (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    work_item_id  int         NOT NULL,
    state         text        NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    version       bigint      NOT NULL DEFAULT 1
);

CREATE TABLE agentd.events (
    seq      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id   bigint      NULL REFERENCES agentd.jobs (id),
    ts       timestamptz NOT NULL DEFAULT now(),
    type     text        NOT NULL,
    payload  jsonb       NOT NULL
);

CREATE INDEX ix_events_job_seq ON agentd.events (job_id, seq);
