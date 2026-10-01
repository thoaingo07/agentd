-- 202610010001 messaging (Phase 2): WaitingForHuman, queued developer replies, conversations,
-- the outbound outbox, inbound idempotency and the v0 user directory.
ALTER TABLE agentd.jobs DROP CONSTRAINT ck_jobs_state;
ALTER TABLE agentd.jobs
    ADD CONSTRAINT ck_jobs_state CHECK (state IN ('Queued', 'Preparing', 'Running', 'WaitingForHuman', 'Publishing', 'Done', 'Failed', 'Cancelled'));

ALTER TABLE agentd.jobs ADD COLUMN pending_messages text[] NOT NULL DEFAULT '{}';

-- job_save gains a parameter: drop the old signature so no stale overload remains.
DROP FUNCTION IF EXISTS agentd.job_save(bigint, bigint, text, text, text, uuid, int, int, int, text, timestamptz, text, text, text, text, timestamptz, jsonb);

-- A job's thread on one provider (Discord thread, Telegram forum topic).
CREATE TABLE agentd.conversations (
    id                        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id                    bigint      NOT NULL REFERENCES agentd.jobs (id) ON DELETE CASCADE,
    provider                  text        NOT NULL,
    external_conversation_id  text        NOT NULL,
    external_space_id         text        NULL,
    link                      text        NULL,
    status_message_id         text        NULL,
    opened_at                 timestamptz NOT NULL,
    closed_at                 timestamptz NULL
);
CREATE UNIQUE INDEX ux_conversations_external ON agentd.conversations (provider, external_conversation_id);
-- At most one open conversation per job and provider.
CREATE UNIQUE INDEX ux_conversations_open_per_provider ON agentd.conversations (job_id, provider) WHERE closed_at IS NULL;

-- Transactional outbox: messages are written with the state change, then delivered by the dispatcher.
CREATE TABLE agentd.outbound_messages (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id               bigint      NULL REFERENCES agentd.jobs (id) ON DELETE CASCADE,
    conversation_id      bigint      NULL REFERENCES agentd.conversations (id) ON DELETE CASCADE,
    provider             text        NOT NULL,
    kind                 text        NOT NULL,
    payload              jsonb       NOT NULL,
    status               text        NOT NULL DEFAULT 'pending'
        CONSTRAINT ck_outbound_status CHECK (status IN ('pending', 'sending', 'sent', 'failed', 'dead')),
    attempts             int         NOT NULL DEFAULT 0,
    next_attempt_at      timestamptz NOT NULL DEFAULT now(),
    external_message_id  text        NULL,
    last_error           text        NULL,
    created_at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_outbound_due ON agentd.outbound_messages (status, next_attempt_at);

-- Inbound idempotency: a provider message is handled once.
CREATE TABLE agentd.inbound_messages (
    provider             text        NOT NULL,
    external_message_id  text        NOT NULL,
    job_id               bigint      NULL REFERENCES agentd.jobs (id) ON DELETE SET NULL,
    user_id              bigint      NULL,
    outcome              text        NOT NULL,
    received_at          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (provider, external_message_id)
);

-- User directory v0 (config-seeded chat allowlist; roles enforced from Phase 5).
CREATE TABLE agentd.users (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text        NOT NULL,
    email       text        NULL,
    roles       text[]      NOT NULL DEFAULT '{}',
    is_active   boolean     NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_users_name ON agentd.users (lower(name));

CREATE TABLE agentd.user_identities (
    user_id      bigint NOT NULL REFERENCES agentd.users (id) ON DELETE CASCADE,
    provider     text   NOT NULL,
    external_id  text   NOT NULL,
    PRIMARY KEY (provider, external_id)
);
CREATE INDEX ix_user_identities_user ON agentd.user_identities (user_id);

ALTER TABLE agentd.inbound_messages
    ADD CONSTRAINT fk_inbound_user FOREIGN KEY (user_id) REFERENCES agentd.users (id) ON DELETE SET NULL;
