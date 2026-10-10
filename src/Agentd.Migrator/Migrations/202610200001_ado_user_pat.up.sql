-- A person can also connect with a personal access token instead of Microsoft's sign-in (docs/architect/ado-user-delegation.md
-- §2.1): `kind` says which secret `refresh_token` holds (still encrypted by the daemon). Their commit name and email
-- (null: from their Azure DevOps profile) go on the commits of jobs assigned to them.
ALTER TABLE agentd.ado_user_connections
    ADD COLUMN kind         text NOT NULL DEFAULT 'OAuth' CONSTRAINT ck_ado_connection_kind CHECK (kind IN ('OAuth', 'Pat')),
    ADD COLUMN commit_name  text NULL,
    ADD COLUMN commit_email text NULL;
