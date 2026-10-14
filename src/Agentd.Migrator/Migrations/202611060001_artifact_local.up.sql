-- Artifacts without S3 are HTML files in a local folder (Agentd:Artifacts:Folder): the row keeps the metadata.
ALTER TABLE agentd.artifacts DROP CONSTRAINT ck_artifact_storage;
ALTER TABLE agentd.artifacts ADD CONSTRAINT ck_artifact_storage CHECK (storage IN ('database', 's3', 'local'));
