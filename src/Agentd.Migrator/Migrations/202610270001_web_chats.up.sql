-- Code questions can start on the Web UI (provider 'web'): no chat thread.
ALTER TABLE agentd.chats ALTER COLUMN thread_id DROP NOT NULL;
