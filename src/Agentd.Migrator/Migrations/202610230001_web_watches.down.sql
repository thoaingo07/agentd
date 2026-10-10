DROP TABLE agentd.pr_watch_messages;
DELETE FROM agentd.pr_watches WHERE thread_id IS NULL;
ALTER TABLE agentd.pr_watches ALTER COLUMN thread_id SET NOT NULL;
