-- The incident monitor (docs/architect/incident-monitor.md).

-- Counts an error group. A known incident always counts (a Resolved one reopens: reopened = true); a new one only with at
-- least p_min_count occurrences (created = true), else nothing is returned. Parallel callers on one fingerprint: one row,
-- every count added, exactly one of them created it.
CREATE OR REPLACE FUNCTION agentd.incident_record(p_app text, p_fingerprint text, p_kind text, p_title text, p_message text, p_operation text,
                                                  p_sample jsonb, p_count bigint, p_first_seen timestamptz, p_last_seen timestamptz, p_min_count int)
RETURNS TABLE (id bigint, created boolean, reopened boolean)
LANGUAGE plpgsql
AS $$
DECLARE
    v_was text;
BEGIN
    IF p_count < p_min_count AND NOT EXISTS (SELECT 1 FROM agentd.incidents AS i WHERE i.app = p_app AND i.fingerprint = p_fingerprint) THEN
        RETURN;
    END IF;

    INSERT INTO agentd.incidents AS i (app, fingerprint, kind, title, message, operation, sample, total_count, first_seen, last_seen)
    VALUES (p_app, p_fingerprint, p_kind, p_title, p_message, p_operation, coalesce(p_sample, '{}'), p_count, p_first_seen, p_last_seen)
    ON CONFLICT (app, fingerprint) DO NOTHING
    RETURNING i.id INTO id;
    IF FOUND THEN
        created := true;
        reopened := false;
        RETURN NEXT;
        RETURN;
    END IF;

    SELECT i.status INTO v_was FROM agentd.incidents AS i WHERE i.app = p_app AND i.fingerprint = p_fingerprint FOR UPDATE;
    UPDATE agentd.incidents AS i
       SET total_count = i.total_count + p_count,
           first_seen  = least(i.first_seen, p_first_seen),
           last_seen   = greatest(i.last_seen, p_last_seen),
           title       = p_title,
           message     = coalesce(p_message, i.message),
           sample      = CASE WHEN p_last_seen >= i.last_seen THEN coalesce(p_sample, i.sample) ELSE i.sample END,
           status      = CASE WHEN i.status = 'Resolved' THEN 'Open' ELSE i.status END,
           updated_at  = now()
     WHERE i.app = p_app AND i.fingerprint = p_fingerprint
    RETURNING i.id INTO id;
    created := false;
    reopened := v_was = 'Resolved';
    RETURN NEXT;
END
$$;

CREATE OR REPLACE FUNCTION agentd.incident_get(p_id bigint)
RETURNS SETOF agentd.incidents
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.incidents WHERE id = p_id $$;

-- Newest activity first; p_status null is any.
CREATE OR REPLACE FUNCTION agentd.incident_list(p_status text, p_limit int)
RETURNS SETOF agentd.incidents
LANGUAGE sql STABLE
AS $$
    SELECT * FROM agentd.incidents
     WHERE p_status IS NULL OR status = p_status
     ORDER BY last_seen DESC, id DESC
     LIMIT greatest(p_limit, 0)
$$;

CREATE OR REPLACE FUNCTION agentd.incident_set_thread(p_id bigint, p_provider text, p_thread_id text, p_space_id text)
RETURNS boolean
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE agentd.incidents
       SET provider = p_provider, thread_id = p_thread_id, space_id = p_space_id, updated_at = now()
     WHERE id = p_id;
    RETURN FOUND;
END
$$;

CREATE OR REPLACE FUNCTION agentd.incident_cursor_get(p_app text)
RETURNS timestamptz
LANGUAGE sql STABLE
AS $$ SELECT checked_until FROM agentd.incident_cursors WHERE app = p_app $$;

-- Moves forward only: a slow pass finishing after a newer one never rewinds the read position.
CREATE OR REPLACE FUNCTION agentd.incident_cursor_set(p_app text, p_checked_until timestamptz)
RETURNS timestamptz
LANGUAGE sql
AS $$
    INSERT INTO agentd.incident_cursors AS c (app, checked_until) VALUES (p_app, p_checked_until)
    ON CONFLICT (app) DO UPDATE SET checked_until = greatest(c.checked_until, excluded.checked_until)
    RETURNING checked_until
$$;

-- Compare-and-set: moves the incident to p_status only from p_from (any status when null). Parallel callers moving it from
-- the same status: exactly one wins.
CREATE OR REPLACE FUNCTION agentd.incident_set_status(p_id bigint, p_status text, p_from text)
RETURNS boolean
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE agentd.incidents
       SET status = p_status, updated_at = now()
     WHERE id = p_id AND (p_from IS NULL OR status = p_from);
    RETURN FOUND;
END
$$;

-- The investigating agent's analysis and its session; an incident still Investigating is Open again (waiting on a person).
CREATE OR REPLACE FUNCTION agentd.incident_set_analysis(p_id bigint, p_analysis text, p_session uuid)
RETURNS boolean
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE agentd.incidents
       SET analysis = p_analysis, session = p_session, analyzed_at = now(),
           status = CASE WHEN status = 'Investigating' THEN 'Open' ELSE status END,
           updated_at = now()
     WHERE id = p_id;
    RETURN FOUND;
END
$$;
