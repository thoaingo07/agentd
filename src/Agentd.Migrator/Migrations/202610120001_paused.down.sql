UPDATE agentd.jobs SET state = 'Cancelled' WHERE state = 'Paused';
ALTER TABLE agentd.jobs DROP CONSTRAINT ck_jobs_state;
ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'WaitingForHuman', 'Publishing', 'InReview', 'Done', 'Failed', 'Cancelled'));
