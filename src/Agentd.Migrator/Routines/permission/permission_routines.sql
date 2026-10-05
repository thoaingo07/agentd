-- Permission requests and remembered approvals (agent tool calls outside the allowlist).

-- Opens a request and records it in the event log (one transaction).
CREATE OR REPLACE FUNCTION agentd.permission_request_insert(p_job_id bigint, p_tool_name text, p_summary text, p_rule_keys text[])
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_id bigint;
BEGIN
    INSERT INTO agentd.permission_requests (job_id, tool_name, summary, rule_keys)
    VALUES (p_job_id, p_tool_name, p_summary, p_rule_keys)
    RETURNING id INTO v_id;

    INSERT INTO agentd.events (job_id, type, payload)
    VALUES (p_job_id, 'permission.requested', jsonb_build_object('id', v_id, 'tool', p_tool_name, 'summary', p_summary, 'ruleKeys', to_jsonb(p_rule_keys)));
    RETURN v_id;
END
$$;

-- Decides a pending request: the first answer wins (chat and the Web UI may answer at once). Allowing
-- for the job or the repository also remembers its rule keys. Returns the request only if this call
-- decided it; nothing if it was already decided.
CREATE OR REPLACE FUNCTION agentd.permission_request_decide(p_id bigint, p_status text, p_scope text, p_decided_by text, p_repo text)
RETURNS SETOF agentd.permission_requests
LANGUAGE plpgsql
AS $$
DECLARE
    v_request agentd.permission_requests;
BEGIN
    UPDATE agentd.permission_requests AS r
       SET status = p_status, scope = p_scope, decided_by = p_decided_by, decided_at = now()
     WHERE r.id = p_id AND r.status = 'pending'
    RETURNING r.* INTO v_request;

    IF v_request.id IS NULL THEN
        RETURN;
    END IF;

    IF p_status = 'allowed' AND p_scope IN ('job', 'repo') THEN
        INSERT INTO agentd.permission_rules (repo, job_id, rule_key, created_by)
        SELECT p_repo, CASE WHEN p_scope = 'job' THEN v_request.job_id END, k, p_decided_by
          FROM unnest(v_request.rule_keys) AS k
        ON CONFLICT DO NOTHING;
    END IF;

    INSERT INTO agentd.events (job_id, type, payload)
    VALUES (v_request.job_id, 'permission.decided',
            jsonb_build_object('id', v_request.id, 'status', p_status, 'scope', p_scope, 'by', p_decided_by, 'summary', v_request.summary));
    RETURN NEXT v_request;
END
$$;

CREATE OR REPLACE FUNCTION agentd.permission_request_get(p_id bigint)
RETURNS SETOF agentd.permission_requests
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.permission_requests WHERE id = p_id $$;

-- A job's open requests, oldest first.
CREATE OR REPLACE FUNCTION agentd.permission_request_list_pending(p_job_id bigint)
RETURNS SETOF agentd.permission_requests
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.permission_requests WHERE job_id = p_job_id AND status = 'pending' ORDER BY id $$;

-- Rule keys remembered for a job: its own, plus its repository's.
CREATE OR REPLACE FUNCTION agentd.permission_rule_keys(p_repo text, p_job_id bigint)
RETURNS SETOF text
LANGUAGE sql STABLE
AS $$ SELECT DISTINCT rule_key FROM agentd.permission_rules WHERE repo = p_repo AND (job_id IS NULL OR job_id = p_job_id) $$;

-- Open requests per job (the dashboard's badge).
CREATE OR REPLACE FUNCTION agentd.permission_request_pending_counts()
RETURNS TABLE (job_id bigint, pending integer)
LANGUAGE sql STABLE
AS $$ SELECT r.job_id, count(*)::integer FROM agentd.permission_requests AS r WHERE r.status = 'pending' GROUP BY r.job_id $$;

-- Remembered approvals, newest first (the Settings page).
CREATE OR REPLACE FUNCTION agentd.permission_rule_list()
RETURNS SETOF agentd.permission_rules
LANGUAGE sql STABLE
AS $$ SELECT * FROM agentd.permission_rules ORDER BY id DESC $$;

-- Revokes a remembered approval and records it in the event log; false when it's already gone.
CREATE OR REPLACE FUNCTION agentd.permission_rule_delete(p_id bigint, p_by text)
RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
    v_rule agentd.permission_rules;
BEGIN
    DELETE FROM agentd.permission_rules AS r WHERE r.id = p_id RETURNING r.* INTO v_rule;
    IF v_rule.id IS NULL THEN
        RETURN false;
    END IF;

    INSERT INTO agentd.events (job_id, type, payload)
    VALUES (v_rule.job_id, 'permission.revoked',
            jsonb_build_object('id', v_rule.id, 'repo', v_rule.repo, 'ruleKey', v_rule.rule_key, 'by', p_by));
    RETURN true;
END
$$;
