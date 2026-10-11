-- What the person asked the reviewer for when starting a review on the Web UI (like !review's --focus and
-- instructions); kept so a review resumed after a restart is asked the same.
ALTER TABLE agentd.review_sessions ADD COLUMN focus text NULL, ADD COLUMN instructions text NULL;
