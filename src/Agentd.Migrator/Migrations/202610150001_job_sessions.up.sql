-- Model profiles (DeepSeek, …): a job's steps may run on different providers, and each provider keeps its own agent
-- session for the job (resuming one provider's conversation on another isn't safe). The approved plan is kept, so a
-- session that starts mid-job gets it in its handoff.
CREATE TABLE agentd.job_sessions (
    job_id     bigint      NOT NULL REFERENCES agentd.jobs (id) ON DELETE CASCADE,
    profile    text        NOT NULL,
    session_id uuid        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (job_id, profile)
);

CREATE TABLE agentd.job_plans (
    job_id       bigint      PRIMARY KEY REFERENCES agentd.jobs (id) ON DELETE CASCADE,
    plan         text        NOT NULL,
    submitted_at timestamptz NOT NULL
);
