DROP TABLE IF EXISTS agentd.inbound_messages;
DROP TABLE IF EXISTS agentd.user_identities;
DROP TABLE IF EXISTS agentd.users;
DROP TABLE IF EXISTS agentd.outbound_messages;
DROP TABLE IF EXISTS agentd.conversations;
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb, text[]);
ALTER TABLE agentd.jobs DROP COLUMN IF EXISTS pending_messages;
ALTER TABLE agentd.jobs DROP CONSTRAINT ck_jobs_state;
ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'Publishing', 'Done', 'Failed', 'Cancelled'));
