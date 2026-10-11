-- The daemon's own log (Settings → System log): agentd's Information and above, everything's warnings, kept
-- Agentd:SystemLog:RetentionDays. In the database, so a Web UI hosted apart from the daemon can show it.
CREATE TABLE agentd.system_logs (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    at        timestamptz NOT NULL,
    level     smallint    NOT NULL CONSTRAINT ck_system_log_level CHECK (level BETWEEN 0 AND 5),   -- Microsoft.Extensions.Logging.LogLevel
    category  text        NOT NULL,
    message   text        NOT NULL,
    exception text        NULL,
    job_id    bigint      NULL
);
CREATE INDEX ix_system_logs_at ON agentd.system_logs (at);
CREATE INDEX ix_system_logs_level_id ON agentd.system_logs (level, id DESC);
