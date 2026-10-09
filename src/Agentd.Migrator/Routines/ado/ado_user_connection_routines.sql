-- Azure DevOps delegated sign-ins (docs/architect/ado-user-delegation.md). Tokens arrive encrypted.

-- Connecting (again) replaces the token and clears a failure: idempotent, and the last of parallel callers wins.
CREATE OR REPLACE FUNCTION agentd.ado_connection_upsert(p_identity uuid, p_unique_name text, p_display_name text, p_web_login text, p_refresh_token bytea)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.ado_user_connections (ado_identity_id, unique_name, display_name, web_login, refresh_token)
    VALUES (p_identity, p_unique_name, p_display_name, p_web_login, p_refresh_token)
    ON CONFLICT (ado_identity_id) DO UPDATE
       SET unique_name = EXCLUDED.unique_name, display_name = EXCLUDED.display_name, web_login = EXCLUDED.web_login,
           refresh_token = EXCLUDED.refresh_token, status = 'Connected', last_error = NULL,
           connected_at = now(), refreshed_at = now()
$$;

CREATE OR REPLACE FUNCTION agentd.ado_connection_find_by_identity(p_identity uuid)
RETURNS SETOF agentd.ado_user_connections
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.ado_user_connections WHERE ado_identity_id = p_identity $$;

CREATE OR REPLACE FUNCTION agentd.ado_connection_find_by_unique_name(p_unique_name text)
RETURNS SETOF agentd.ado_user_connections
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.ado_user_connections WHERE lower(unique_name) = lower(p_unique_name) ORDER BY connected_at DESC LIMIT 1 $$;

CREATE OR REPLACE FUNCTION agentd.ado_connection_list_by_web_login(p_web_login text)
RETURNS SETOF agentd.ado_user_connections
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.ado_user_connections WHERE web_login = p_web_login ORDER BY connected_at $$;

-- Entra rotates the refresh token on use; a stored failure is cleared by a refresh that worked.
CREATE OR REPLACE FUNCTION agentd.ado_connection_store_token(p_identity uuid, p_refresh_token bytea)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH updated AS (
        UPDATE agentd.ado_user_connections
           SET refresh_token = p_refresh_token, status = 'Connected', last_error = NULL, refreshed_at = now()
         WHERE ado_identity_id = p_identity
        RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM updated)
$$;

CREATE OR REPLACE FUNCTION agentd.ado_connection_mark_failed(p_identity uuid, p_error text)
RETURNS void
LANGUAGE sql
AS $$ UPDATE agentd.ado_user_connections SET status = 'Failed', last_error = p_error WHERE ado_identity_id = p_identity $$;

-- Only the web login that connected it may remove it.
CREATE OR REPLACE FUNCTION agentd.ado_connection_delete(p_identity uuid, p_web_login text)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH deleted AS (
        DELETE FROM agentd.ado_user_connections WHERE ado_identity_id = p_identity AND web_login = p_web_login RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM deleted)
$$;
