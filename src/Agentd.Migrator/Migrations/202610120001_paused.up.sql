-- A developer can pause a job and resume it later (the session, worktree and branch are kept).
ALTER TABLE agentd.jobs DROP CONSTRAINT ck_jobs_state;
ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'WaitingForHuman', 'Publishing', 'InReview', 'Paused', 'Done', 'Failed', 'Cancelled'));
