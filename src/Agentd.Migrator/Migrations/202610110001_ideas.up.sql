-- Brainstormed ideas (Phase 2d): a chat thread where the agent explores an idea against the code and
-- proposes work items. Separate from jobs: an idea has no work item yet.
CREATE TABLE agentd.ideas (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo               text        NOT NULL,
    title              text        NOT NULL,
    author             text        NOT NULL,
    provider           text        NOT NULL,
    thread_id          text        NOT NULL,
    space_id           text        NULL,
    status             text        NOT NULL DEFAULT 'Brainstorming'
        CONSTRAINT ck_idea_status CHECK (status IN ('Brainstorming', 'Proposed', 'Created', 'Discarded', 'Closed')),
    session_id         uuid        NULL,
    model              text        NULL,             -- --model for its turns (alias or full name); null = the default
    effort             text        NULL,             -- --effort: low, medium, high, xhigh, max; null = the default
    worktree_path      text        NULL,
    drafts             jsonb       NULL,
    created_work_items int[]       NOT NULL DEFAULT '{}',
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_ideas_thread ON agentd.ideas (provider, thread_id);

-- The idea's conversation, both directions (the future Ideas page reads it; the thread may be deleted).
CREATE TABLE agentd.idea_messages (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    idea_id    bigint      NOT NULL REFERENCES agentd.ideas (id) ON DELETE CASCADE,
    direction  text        NOT NULL CONSTRAINT ck_idea_message_direction CHECK (direction IN ('in', 'out')),
    author     text        NOT NULL,
    text       text        NOT NULL,
    at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_idea_messages ON agentd.idea_messages (idea_id, id);
