-- A review page's Send to a PR, kept for the re-checks after pushes (review-sessions.md §4): the findings as posted (each
-- with its PR thread and status), the main message's thread, the PR head they're about, and each re-check's counts.
CREATE TABLE agentd.review_session_posts (
    session_id      bigint      PRIMARY KEY REFERENCES agentd.review_sessions (id) ON DELETE CASCADE,
    pull_request_id int         NOT NULL,
    posted          jsonb       NOT NULL,
    head            text        NULL,
    rechecks        jsonb       NOT NULL DEFAULT '[]'::jsonb,
    done            boolean     NOT NULL DEFAULT false,
    updated_at      timestamptz NOT NULL DEFAULT now()
);
