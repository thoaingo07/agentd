-- A job's session on one model profile, created on first use. Safe under concurrent callers: the first insert wins
-- and everyone gets that session (created = true only for the caller whose insert it was).
CREATE OR REPLACE FUNCTION agentd.job_session_get_or_create(p_job_id bigint, p_profile text, p_session_id uuid)
RETURNS TABLE (session_id uuid, created boolean)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO agentd.job_sessions (job_id, profile, session_id) VALUES (p_job_id, p_profile, p_session_id)
    ON CONFLICT (job_id, profile) DO NOTHING;
    IF FOUND THEN
        RETURN QUERY SELECT p_session_id, true;
    ELSE
        RETURN QUERY SELECT s.session_id, false FROM agentd.job_sessions s WHERE s.job_id = p_job_id AND s.profile = p_profile;
    END IF;
END
$$;

-- The latest plan the agent submitted for the job (a resubmitted plan replaces it).
CREATE OR REPLACE FUNCTION agentd.job_plan_save(p_job_id bigint, p_plan text, p_submitted_at timestamptz)
RETURNS void
LANGUAGE sql
AS $$
    INSERT INTO agentd.job_plans (job_id, plan, submitted_at) VALUES (p_job_id, p_plan, p_submitted_at)
    ON CONFLICT (job_id) DO UPDATE SET plan = EXCLUDED.plan, submitted_at = EXCLUDED.submitted_at
$$;

CREATE OR REPLACE FUNCTION agentd.job_plan_get(p_job_id bigint)
RETURNS text
LANGUAGE sql STABLE
AS $$ SELECT plan FROM agentd.job_plans WHERE job_id = p_job_id $$;
