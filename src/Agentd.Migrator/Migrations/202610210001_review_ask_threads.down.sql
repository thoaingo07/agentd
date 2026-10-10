DROP INDEX agentd.ix_review_asks_thread;
ALTER TABLE agentd.review_asks DROP COLUMN agent_session, DROP COLUMN thread_id;
