-- PRs can be watched from the Web UI (provider 'web'): no 👀 thread. Every notice the monitor writes is kept, so the
-- Web UI shows what a thread would.
ALTER TABLE agentd.pr_watches ALTER COLUMN thread_id DROP NOT NULL;

CREATE TABLE agentd.pr_watch_messages (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    watch_id   bigint      NOT NULL REFERENCES agentd.pr_watches (id) ON DELETE CASCADE,
    text       text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_pr_watch_messages ON agentd.pr_watch_messages (watch_id, id);
