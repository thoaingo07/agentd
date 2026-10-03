-- 202610030001 waiting for a developer: when the wait started and how many reminders were sent.
ALTER TABLE agentd.jobs
    ADD COLUMN waiting_since  timestamptz NULL,
    ADD COLUMN wait_reminders int         NOT NULL DEFAULT 0;

-- job_save gains two parameters: drop the old signature so no stale overload remains.
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[]);
