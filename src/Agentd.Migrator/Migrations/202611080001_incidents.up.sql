-- The incident monitor (docs/architect/incident-monitor.md): error groups found in apps' Application Insights telemetry,
-- one row per (app, fingerprint), and where each app's last read ended.
CREATE TABLE agentd.incidents (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    app           text        NOT NULL,
    fingerprint   text        NOT NULL,
    kind          text        NOT NULL CONSTRAINT ck_incident_kind CHECK (kind IN ('exception', 'request')),
    title         text        NOT NULL,
    message       text        NULL,
    operation     text        NULL,
    sample        jsonb       NOT NULL DEFAULT '{}',   -- the latest occurrence: stack top, result code, operation id
    status        text        NOT NULL DEFAULT 'Open'
                              CONSTRAINT ck_incident_status CHECK (status IN ('Open', 'Investigating', 'Fixing', 'Resolved', 'Ignored')),
    total_count   bigint      NOT NULL DEFAULT 0,
    first_seen    timestamptz NOT NULL,
    last_seen     timestamptz NOT NULL,
    provider      text        NULL,                    -- the 🚨 thread, when chat is on
    thread_id     text        NULL,
    space_id      text        NULL,
    work_item_id  int         NULL,                    -- the Bug filed for a fix (next version)
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_incidents_fingerprint UNIQUE (app, fingerprint)
);
CREATE INDEX ix_incidents_status ON agentd.incidents (status, last_seen DESC);

CREATE TABLE agentd.incident_cursors (
    app            text        PRIMARY KEY,
    checked_until  timestamptz NOT NULL
);
