-- Ideas can start on the Web UI (provider 'web'): they have no chat thread.
ALTER TABLE agentd.ideas ALTER COLUMN thread_id DROP NOT NULL;
