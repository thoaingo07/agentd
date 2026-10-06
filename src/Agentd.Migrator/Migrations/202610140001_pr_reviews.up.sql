-- PR reviews in chat (!review): a read-only agent session on the PR head; findings stay in the thread until a
-- person posts them to the PR. Separate from jobs (no work item) and from ideas (other choices and data).
CREATE TABLE agentd.pr_reviews (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo            text        NOT NULL,
    pull_request_id int         NOT NULL,
    title           text        NOT NULL,
    author          text        NOT NULL,             -- who asked for the review
    provider        text        NOT NULL,
    thread_id       text        NOT NULL,
    space_id        text        NULL,
    status          text        NOT NULL DEFAULT 'Reviewing'
        CONSTRAINT ck_pr_review_status CHECK (status IN ('Reviewing', 'Reviewed', 'Posted', 'Kept', 'Discarded', 'Closed')),
    head_commit     text        NULL,                 -- the PR head the findings are about
    focus           text        NULL,                 -- --focus, e.g. "security,tests"
    session_id      uuid        NULL,
    model           text        NULL,
    effort          text        NULL,
    worktree_path   text        NULL,
    result          jsonb       NULL,                 -- { summary, findings: [ … ] }, the latest
    posted_threads  int[]       NOT NULL DEFAULT '{}',-- the PR comment threads agentd opened
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_pr_reviews_thread ON agentd.pr_reviews (provider, thread_id);
CREATE INDEX ix_pr_reviews_pr ON agentd.pr_reviews (repo, pull_request_id);

CREATE TABLE agentd.pr_review_messages (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    review_id  bigint      NOT NULL REFERENCES agentd.pr_reviews (id) ON DELETE CASCADE,
    direction  text        NOT NULL CONSTRAINT ck_pr_review_message_direction CHECK (direction IN ('in', 'out')),
    author     text        NOT NULL,
    text       text        NOT NULL,
    at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_pr_review_messages ON agentd.pr_review_messages (review_id, id);
