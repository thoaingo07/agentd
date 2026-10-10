DELETE FROM agentd.ideas WHERE thread_id IS NULL;
ALTER TABLE agentd.ideas ALTER COLUMN thread_id SET NOT NULL;
