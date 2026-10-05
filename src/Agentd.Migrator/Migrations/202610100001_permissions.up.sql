-- Permission requests: a tool call outside the agent's allowlist, waiting for a person to allow or deny it.
CREATE TABLE agentd.permission_requests (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id        bigint      NOT NULL REFERENCES agentd.jobs (id) ON DELETE CASCADE,
    tool_name     text        NOT NULL,
    summary       text        NOT NULL,          -- what the agent wants, for people (e.g. the shell command)
    rule_keys     text[]      NOT NULL,          -- what "allow for this job / always" remembers (e.g. Bash(npm install:*))
    status        text        NOT NULL DEFAULT 'pending'
        CONSTRAINT ck_permission_status CHECK (status IN ('pending', 'allowed', 'denied', 'expired')),
    scope         text        NULL
        CONSTRAINT ck_permission_scope CHECK (scope IN ('once', 'job', 'repo')),
    decided_by    text        NULL,
    requested_at  timestamptz NOT NULL DEFAULT now(),
    decided_at    timestamptz NULL
);
CREATE INDEX ix_permission_requests_pending ON agentd.permission_requests (job_id, id) WHERE status = 'pending';

-- Remembered approvals: for one job (job_id set) or for every job of a repository (job_id null).
CREATE TABLE agentd.permission_rules (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo        text        NOT NULL,
    job_id      bigint      NULL REFERENCES agentd.jobs (id) ON DELETE CASCADE,
    rule_key    text        NOT NULL,
    created_by  text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_permission_rules ON agentd.permission_rules (repo, coalesce(job_id, 0), rule_key);
