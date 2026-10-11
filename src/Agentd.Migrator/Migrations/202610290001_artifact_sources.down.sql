ALTER TABLE agentd.artifacts ADD COLUMN job_id bigint NULL, ADD COLUMN chat_id bigint NULL;
UPDATE agentd.artifacts SET job_id = CASE WHEN source = 'job' THEN source_id END, chat_id = CASE WHEN source = 'chat' THEN source_id END;
DELETE FROM agentd.artifacts WHERE source NOT IN ('job', 'chat', 'web');
DROP INDEX agentd.ux_artifacts_source_title;
ALTER TABLE agentd.artifacts DROP COLUMN source, DROP COLUMN source_id;
CREATE UNIQUE INDEX ux_artifacts_source_title ON agentd.artifacts (job_id, chat_id, title) NULLS NOT DISTINCT;
