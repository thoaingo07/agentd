DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[], timestamptz, int, text, jsonb, int, jsonb);
ALTER TABLE agentd.jobs DROP COLUMN IF EXISTS review_state, DROP COLUMN IF EXISTS fix_rounds;
ALTER TABLE agentd.jobs DROP CONSTRAINT ck_jobs_state;
ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'WaitingForHuman', 'Publishing', 'Done', 'Failed', 'Cancelled'));
