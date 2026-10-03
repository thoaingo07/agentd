-- 202610040001 plan approval: whether the plan needs the developer's OK, and the agent's estimate.
ALTER TABLE agentd.jobs
    ADD COLUMN plan_status   text  NOT NULL DEFAULT 'NotRequired'
        CONSTRAINT ck_jobs_plan_status CHECK (plan_status IN ('NotRequired', 'Pending', 'Approved')),
    ADD COLUMN plan_estimate jsonb NULL;

-- job_save gains two parameters: drop the old signature so no stale overload remains.
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[], timestamptz, int);
