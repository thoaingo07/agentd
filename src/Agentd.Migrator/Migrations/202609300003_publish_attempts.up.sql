-- 202609300003 publish retries: count failed push/PR attempts while Publishing.
ALTER TABLE agentd.jobs ADD COLUMN publish_attempts int NOT NULL DEFAULT 0;

-- job_save gains a parameter; CREATE OR REPLACE would add an overload instead of replacing it,
-- so drop the old signature here (routines are re-created after migrations).
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb);
