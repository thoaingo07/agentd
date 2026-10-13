-- Reviews started in chat show on the Reviews page (docs/architect/review-sessions.md §1): the conversation they live in,
-- and at most one session per chat !review of a PR (pr_review_id), which the chat review's findings fill.
ALTER TABLE agentd.review_sessions ADD COLUMN chat_link text NULL;
CREATE UNIQUE INDEX ux_review_sessions_pr_review ON agentd.review_sessions (pr_review_id) WHERE pr_review_id IS NOT NULL;
