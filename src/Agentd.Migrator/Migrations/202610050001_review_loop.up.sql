-- 202610050001 review loop: the InReview state, fix rounds and PR review tracking.
ALTER TABLE agentd.jobs DROP CONSTRAINT ck_jobs_state;
ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'WaitingForHuman', 'Publishing', 'InReview', 'Done', 'Failed', 'Cancelled'));

ALTER TABLE agentd.jobs
    ADD COLUMN fix_rounds   int   NOT NULL DEFAULT 0,
    ADD COLUMN review_state jsonb NULL;

-- job_save gains two parameters: drop the old signature so no stale overload remains.
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[], timestamptz, int, text, jsonb);
