DELETE FROM agentd.artifacts WHERE storage = 'local';
ALTER TABLE agentd.artifacts DROP CONSTRAINT ck_artifact_storage;
ALTER TABLE agentd.artifacts ADD CONSTRAINT ck_artifact_storage CHECK (storage IN ('database', 's3'));
