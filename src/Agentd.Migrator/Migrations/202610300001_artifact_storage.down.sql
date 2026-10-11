DELETE FROM agentd.artifacts WHERE storage = 's3';
DROP INDEX agentd.ix_artifacts_live_updated;
CREATE INDEX ix_artifacts_live_updated ON agentd.artifacts (updated_at) WHERE html IS NOT NULL;
ALTER TABLE agentd.artifacts DROP COLUMN storage;
