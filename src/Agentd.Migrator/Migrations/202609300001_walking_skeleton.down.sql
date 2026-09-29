DROP INDEX IF EXISTS agentd.ix_jobs_state;
DROP INDEX IF EXISTS agentd.ix_jobs_queued;
DROP INDEX IF EXISTS agentd.ux_jobs_active_work_item;
ALTER TABLE agentd.jobs DROP CONSTRAINT IF EXISTS ck_jobs_state;
ALTER TABLE agentd.jobs
    DROP COLUMN repo, DROP COLUMN title, DROP COLUMN branch, DROP COLUMN worktree_path,
    DROP COLUMN claude_session_id, DROP COLUMN attempt, DROP COLUMN resume_count, DROP COLUMN last_error,
    DROP COLUMN not_before, DROP COLUMN pr_title, DROP COLUMN pr_description, DROP COLUMN pr_summary,
    DROP COLUMN pr_url, DROP COLUMN claimed_by;
