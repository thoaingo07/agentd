-- Saves a job's state and appends its domain events atomically, guarded by the expected version.
-- Raises AG409 if the stored version differs (concurrent change) and AG404 if the job doesn't exist.
CREATE OR REPLACE FUNCTION agentd.job_save(
    p_id bigint, p_expected_version bigint,
    p_state text, p_branch text, p_worktree_path text, p_claude_session_id uuid,
    p_attempt int, p_resume_count int, p_publish_attempts int, p_last_error text, p_not_before timestamptz,
    p_pr_title text, p_pr_description text, p_pr_summary text, p_pr_url text,
    p_updated_at timestamptz, p_events jsonb)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_version bigint;
BEGIN
    UPDATE agentd.jobs AS j
       SET state = p_state, branch = p_branch, worktree_path = p_worktree_path,
           claude_session_id = p_claude_session_id, attempt = p_attempt, resume_count = p_resume_count,
           publish_attempts = p_publish_attempts,
           last_error = p_last_error, not_before = p_not_before,
           pr_title = p_pr_title, pr_description = p_pr_description, pr_summary = p_pr_summary, pr_url = p_pr_url,
           updated_at = p_updated_at, version = j.version + 1
     WHERE j.id = p_id AND j.version = p_expected_version
    RETURNING j.version INTO v_version;

    IF v_version IS NULL THEN
        IF EXISTS (SELECT 1 FROM agentd.jobs WHERE jobs.id = p_id) THEN
            RAISE EXCEPTION 'job % was changed concurrently (expected version %)', p_id, p_expected_version USING ERRCODE = 'AG409';
        END IF;
        RAISE EXCEPTION 'job % not found', p_id USING ERRCODE = 'AG404';
    END IF;

    INSERT INTO agentd.events (job_id, type, payload)
    SELECT p_id, e ->> 'type', e -> 'payload' FROM jsonb_array_elements(coalesce(p_events, '[]'::jsonb)) AS e;

    RETURN v_version;
END
$$;
