-- 202609300001 walking skeleton: full job record, one active job per work item, dequeue index.
ALTER TABLE agentd.jobs
    ADD COLUMN repo              text        NOT NULL DEFAULT '',
    ADD COLUMN title             text        NOT NULL DEFAULT '',
    ADD COLUMN branch            text        NULL,
    ADD COLUMN worktree_path     text        NULL,
    ADD COLUMN claude_session_id uuid        NULL,
    ADD COLUMN attempt           int         NOT NULL DEFAULT 1,
    ADD COLUMN resume_count      int         NOT NULL DEFAULT 0,
    ADD COLUMN last_error        text        NULL,
    ADD COLUMN not_before        timestamptz NULL,
    ADD COLUMN pr_title          text        NULL,
    ADD COLUMN pr_description    text        NULL,
    ADD COLUMN pr_summary        text        NULL,
    ADD COLUMN pr_url            text        NULL,
    ADD COLUMN claimed_by        text        NULL;

ALTER TABLE agentd.jobs ALTER COLUMN repo DROP DEFAULT;

ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'Publishing', 'Done', 'Failed', 'Cancelled'));

-- Only one active (non-terminal) job per work item.
CREATE UNIQUE INDEX ux_jobs_active_work_item ON agentd.jobs (work_item_id)
    WHERE state NOT IN ('Done', 'Failed', 'Cancelled');

-- Dequeue: oldest runnable queued job.
CREATE INDEX ix_jobs_queued ON agentd.jobs (created_at) WHERE state = 'Queued';

CREATE INDEX ix_jobs_state ON agentd.jobs (state);
