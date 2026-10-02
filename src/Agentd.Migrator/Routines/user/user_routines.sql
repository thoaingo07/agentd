-- User directory v0. Configuration is the source of truth: user_sync makes the tables match it.
-- p_users: [{ "name": "...", "email": "...", "roles": ["Admin"], "identities": [{ "provider": "discord", "externalId": "..." }] }]
-- Users missing from p_users are deactivated, never deleted (audit trail). An identity moves to the
-- user that now lists it.
CREATE OR REPLACE FUNCTION agentd.user_sync(p_users jsonb)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    -- Serialize concurrent syncs (two daemons starting at once).
    PERFORM pg_advisory_xact_lock(hashtext('agentd.user_sync'));

    DROP TABLE IF EXISTS tmp_users, tmp_identities;
    CREATE TEMP TABLE tmp_users ON COMMIT DROP AS
    SELECT u ->> 'name' AS name, u ->> 'email' AS email,
           coalesce(ARRAY(SELECT jsonb_array_elements_text(u -> 'roles')), '{}') AS roles,
           coalesce(u -> 'identities', '[]'::jsonb) AS identities
      FROM jsonb_array_elements(coalesce(p_users, '[]'::jsonb)) AS u;

    UPDATE agentd.users AS x
       SET email = t.email, roles = t.roles, is_active = true
      FROM tmp_users AS t
     WHERE lower(x.name) = lower(t.name);

    INSERT INTO agentd.users (name, email, roles)
    SELECT t.name, t.email, t.roles FROM tmp_users AS t
     WHERE NOT EXISTS (SELECT 1 FROM agentd.users AS x WHERE lower(x.name) = lower(t.name));

    UPDATE agentd.users AS x SET is_active = false
     WHERE x.is_active AND NOT EXISTS (SELECT 1 FROM tmp_users AS t WHERE lower(t.name) = lower(x.name));

    CREATE TEMP TABLE tmp_identities ON COMMIT DROP AS
    SELECT x.id AS user_id, i ->> 'provider' AS provider, i ->> 'externalId' AS external_id
      FROM tmp_users AS t
      JOIN agentd.users AS x ON lower(x.name) = lower(t.name)
     CROSS JOIN LATERAL jsonb_array_elements(t.identities) AS i;

    -- Replace the identities of every listed or deactivated user, and free identities claimed by someone else.
    DELETE FROM agentd.user_identities AS ui
     USING agentd.users AS x
     WHERE ui.user_id = x.id
       AND (NOT x.is_active OR EXISTS (SELECT 1 FROM tmp_users AS t WHERE lower(t.name) = lower(x.name)));
    DELETE FROM agentd.user_identities AS ui
     WHERE (ui.provider, ui.external_id) IN (SELECT ti.provider, ti.external_id FROM tmp_identities AS ti);

    INSERT INTO agentd.user_identities (user_id, provider, external_id)
    SELECT ti.user_id, ti.provider, ti.external_id FROM tmp_identities AS ti;
END
$$;

-- The active or inactive user owning a chat identity (callers check is_active).
CREATE OR REPLACE FUNCTION agentd.user_find_by_identity(p_provider text, p_external_id text)
RETURNS SETOF agentd.users
LANGUAGE sql STABLE
AS $$
    SELECT u.* FROM agentd.users AS u
      JOIN agentd.user_identities AS ui ON ui.user_id = u.id
     WHERE ui.provider = p_provider AND ui.external_id = p_external_id
$$;
