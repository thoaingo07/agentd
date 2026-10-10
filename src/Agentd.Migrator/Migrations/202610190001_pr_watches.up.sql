-- The PR Monitor's watch list (docs/architect/pr-reviewer-and-monitor.md §2.0): PRs someone asked agentd to watch, with
-- the 👀 thread where it reports and asks, what it already handled, and a prepared fix round waiting for an answer.
CREATE TABLE agentd.pr_watches (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo             text        NOT NULL,
    pull_request_id  int         NOT NULL,
    title            text        NOT NULL,
    watched_by       text        NOT NULL,
    provider         text        NOT NULL,
    thread_id        text        NOT NULL,
    space_id         text        NULL,
    status           text        NOT NULL DEFAULT 'Watching' CONSTRAINT ck_pr_watch_status CHECK (status IN ('Watching', 'Stopped')),
    fix_rounds       int         NOT NULL DEFAULT 0,
    seen_comments    int[]       NOT NULL DEFAULT '{}', -- comment ids already handled (or present when watching began)
    last_build_id    int         NULL,                   -- the latest failed PR build already handled
    signal_at        timestamptz NULL,                   -- the first unhandled signal (debounce)
    pending          jsonb       NULL,                   -- a prepared fix round waiting for push / discard
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_pr_watches_active ON agentd.pr_watches (repo, pull_request_id) WHERE status = 'Watching';
CREATE UNIQUE INDEX ux_pr_watches_thread ON agentd.pr_watches (provider, thread_id);
