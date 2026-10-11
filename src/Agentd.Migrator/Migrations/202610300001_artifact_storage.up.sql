-- Artifacts can live in S3-compatible storage (Cloudflare R2): the row keeps the metadata and where the HTML is.
-- Expired is now expired_at (an R2 page has no html in the row while it's alive).
ALTER TABLE agentd.artifacts ADD COLUMN storage text NOT NULL DEFAULT 'database'
    CONSTRAINT ck_artifact_storage CHECK (storage IN ('database', 's3'));
DROP INDEX agentd.ix_artifacts_live_updated;
CREATE INDEX ix_artifacts_live_updated ON agentd.artifacts (updated_at) WHERE expired_at IS NULL;
