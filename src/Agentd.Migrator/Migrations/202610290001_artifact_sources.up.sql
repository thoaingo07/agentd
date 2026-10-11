-- Artifacts can come from any agent turn, not only jobs and chats: a source kind (job, chat, idea, review,
-- review-session, watch, web) and its id replace job_id / chat_id. The same source publishing a title again still
-- replaces its page.
ALTER TABLE agentd.artifacts ADD COLUMN source text, ADD COLUMN source_id bigint;
UPDATE agentd.artifacts
   SET source = CASE WHEN job_id IS NOT NULL THEN 'job' WHEN chat_id IS NOT NULL THEN 'chat' ELSE 'web' END,
       source_id = coalesce(job_id, chat_id, 0);
ALTER TABLE agentd.artifacts ALTER COLUMN source SET NOT NULL, ALTER COLUMN source_id SET NOT NULL;
DROP INDEX agentd.ux_artifacts_source_title;
ALTER TABLE agentd.artifacts DROP COLUMN job_id, DROP COLUMN chat_id;
CREATE UNIQUE INDEX ux_artifacts_source_title ON agentd.artifacts (source, source_id, title);
