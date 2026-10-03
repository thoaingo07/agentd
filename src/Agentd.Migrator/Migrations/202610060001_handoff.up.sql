-- 202610060001 knowledge hand-off after the PR is merged.
ALTER TABLE agentd.jobs
    ADD COLUMN handoff_status text NOT NULL DEFAULT 'None'
        CONSTRAINT ck_jobs_handoff_status CHECK (handoff_status IN ('None', 'Requested', 'Proposing', 'Agreed', 'Declined', 'Closing'));

-- job_save gains a parameter: drop the old signature so no stale overload remains.
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[], timestamptz, int, text, jsonb, int, jsonb);
