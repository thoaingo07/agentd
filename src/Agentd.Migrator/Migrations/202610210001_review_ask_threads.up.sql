-- Ask on a review page becomes a conversation: follow-ups belong to the thread's first question (thread_id), and the
-- first question holds the agent's Claude session so follow-ups resume it.
ALTER TABLE agentd.review_asks
    ADD COLUMN thread_id     bigint NULL REFERENCES agentd.review_asks (id) ON DELETE CASCADE,
    ADD COLUMN agent_session uuid   NULL;
CREATE INDEX ix_review_asks_thread ON agentd.review_asks (thread_id, id) WHERE thread_id IS NOT NULL;
