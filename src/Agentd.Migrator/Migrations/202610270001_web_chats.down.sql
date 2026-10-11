DELETE FROM agentd.chats WHERE thread_id IS NULL;
ALTER TABLE agentd.chats ALTER COLUMN thread_id SET NOT NULL;
