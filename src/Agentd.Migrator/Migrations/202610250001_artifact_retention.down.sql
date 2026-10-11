DROP INDEX agentd.ix_artifacts_live_updated;
DELETE FROM agentd.artifacts WHERE html IS NULL;
ALTER TABLE agentd.artifacts DROP COLUMN expired_at;
ALTER TABLE agentd.artifacts ALTER COLUMN html SET NOT NULL;
