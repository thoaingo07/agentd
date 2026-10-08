-- !chat: a read-only agent answering questions in a chat thread, over every registered repository's code (and, later,
-- Azure DevOps through agentd's tools). It stays open until someone closes it.
CREATE TABLE agentd.chats (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    author      text        NOT NULL,
    provider    text        NOT NULL,
    thread_id   text        NOT NULL,
    space_id    text        NULL,
    status      text        NOT NULL DEFAULT 'Open' CONSTRAINT ck_chat_status CHECK (status IN ('Open', 'Closed')),
    repos       text[]      NOT NULL,             -- the repositories it sees
    session_id  uuid        NULL,
    model       text        NULL,
    effort      text        NULL,
    worktrees   text[]      NOT NULL DEFAULT '{}',-- its read-only checkouts, one per repository
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_chats_thread ON agentd.chats (provider, thread_id);

CREATE TABLE agentd.chat_messages (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    chat_id    bigint      NOT NULL REFERENCES agentd.chats (id) ON DELETE CASCADE,
    direction  text        NOT NULL CONSTRAINT ck_chat_message_direction CHECK (direction IN ('in', 'out')),
    author     text        NOT NULL,
    text       text        NOT NULL,
    at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_chat_messages_chat ON agentd.chat_messages (chat_id, id);
