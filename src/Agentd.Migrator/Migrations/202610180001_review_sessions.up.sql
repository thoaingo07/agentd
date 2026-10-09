-- Review sessions (docs/architect/review-sessions.md): a review of any PR, branch, commit range or uploaded diff, worked
-- on a page: findings with the person's decision, their comments, and questions about the code.
CREATE TABLE agentd.review_sessions (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo            text        NOT NULL,
    target          text        NOT NULL CONSTRAINT ck_review_session_target CHECK (target IN ('pr', 'branch', 'range', 'upload')),
    pull_request_id int         NULL,
    head_ref        text        NULL,                 -- the branch (or commit) asked for
    base_ref        text        NULL,                 -- what it's compared with
    base_commit     text        NULL,                 -- pinned when it starts
    head_commit     text        NULL,
    status          text        NOT NULL DEFAULT 'Reviewing'
        CONSTRAINT ck_review_session_status CHECK (status IN ('Reviewing', 'Ready', 'Sent', 'Closed', 'Failed')),
    error           text        NULL,
    model           text        NULL,
    effort          text        NULL,
    summary         text        NULL,
    findings        jsonb       NOT NULL DEFAULT '[]', -- [{ severity, file, line, title, detail, suggestion, decision, edited }]
    pr_review_id    bigint      NULL REFERENCES agentd.pr_reviews (id) ON DELETE SET NULL,
    worktree_path   text        NULL,
    created_by      text        NOT NULL,
    sent_to         text        NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_review_sessions_creator ON agentd.review_sessions (created_by, id DESC);
CREATE INDEX ix_review_sessions_pr ON agentd.review_sessions (repo, pull_request_id) WHERE pull_request_id IS NOT NULL;

CREATE TABLE agentd.review_comments (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id  bigint      NOT NULL REFERENCES agentd.review_sessions (id) ON DELETE CASCADE,
    file        text        NULL,                     -- null: the whole change
    line        int         NULL,
    end_line    int         NULL,
    text        text        NOT NULL,
    author      text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_review_comments_session ON agentd.review_comments (session_id, id);

CREATE TABLE agentd.review_asks (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id  bigint      NOT NULL REFERENCES agentd.review_sessions (id) ON DELETE CASCADE,
    file        text        NULL,
    line        int         NULL,
    end_line    int         NULL,
    question    text        NOT NULL,
    answer      text        NULL,
    author      text        NOT NULL,
    asked_at    timestamptz NOT NULL DEFAULT now(),
    answered_at timestamptz NULL
);
CREATE INDEX ix_review_asks_session ON agentd.review_asks (session_id, id);
