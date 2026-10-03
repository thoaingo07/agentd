DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[], timestamptz, int, text, jsonb, int, jsonb, text);
ALTER TABLE agentd.jobs DROP COLUMN IF EXISTS handoff_status;
