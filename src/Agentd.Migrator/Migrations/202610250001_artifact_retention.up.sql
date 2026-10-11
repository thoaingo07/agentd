-- Artifacts expire (Agentd:Artifacts:RetentionDays after their last update): the HTML goes, a tombstone stays so the
-- link can say it expired, and publishing the same title again from the same source brings the page back.
ALTER TABLE agentd.artifacts ALTER COLUMN html DROP NOT NULL;
ALTER TABLE agentd.artifacts ADD COLUMN expired_at timestamptz NULL;
CREATE INDEX ix_artifacts_live_updated ON agentd.artifacts (updated_at) WHERE html IS NOT NULL;
