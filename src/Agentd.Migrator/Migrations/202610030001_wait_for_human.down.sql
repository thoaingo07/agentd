DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[], timestamptz, int);
ALTER TABLE agentd.jobs DROP COLUMN IF EXISTS wait_reminders, DROP COLUMN IF EXISTS waiting_since;
