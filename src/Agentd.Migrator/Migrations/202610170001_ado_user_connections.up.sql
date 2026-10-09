-- Azure DevOps on behalf of a person (docs/architect/ado-user-delegation.md): each person's delegated sign-in, keyed by
-- their Azure DevOps identity. The refresh token is encrypted by the daemon (Data Protection) before it gets here.
CREATE TABLE agentd.ado_user_connections (
    ado_identity_id uuid        PRIMARY KEY,
    unique_name     text        NOT NULL,
    display_name    text        NOT NULL,
    web_login       text        NOT NULL,
    refresh_token   bytea       NOT NULL,
    status          text        NOT NULL DEFAULT 'Connected' CONSTRAINT ck_ado_connection_status CHECK (status IN ('Connected', 'Failed')),
    last_error      text        NULL,
    connected_at    timestamptz NOT NULL DEFAULT now(),
    refreshed_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_ado_user_connections_unique_name ON agentd.ado_user_connections (lower(unique_name));
CREATE INDEX ix_ado_user_connections_web_login ON agentd.ado_user_connections (web_login);
