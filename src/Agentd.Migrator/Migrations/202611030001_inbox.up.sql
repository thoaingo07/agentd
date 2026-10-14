-- The Inbox (docs/architect/inbox.md): every message agentd sends a person, one thread per source, whatever chat is set
-- up. Read marks are per person and thread.
CREATE TABLE agentd.inbox_items (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source     text        NOT NULL CONSTRAINT ck_inbox_source CHECK (source IN ('job', 'idea', 'review', 'chat', 'watch')),
    source_id  bigint      NOT NULL,
    title      text        NOT NULL,
    recipient  text        NULL,
    kind       text        NOT NULL,
    markdown   text        NOT NULL,
    options    jsonb       NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_inbox_items_thread ON agentd.inbox_items (source, source_id, id DESC);
CREATE INDEX ix_inbox_items_created ON agentd.inbox_items (created_at);

CREATE TABLE agentd.inbox_reads (
    user_name  text   NOT NULL,
    source     text   NOT NULL,
    source_id  bigint NOT NULL,
    read_until bigint NOT NULL,
    PRIMARY KEY (user_name, source, source_id)
);
