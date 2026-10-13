-- The prompt log (docs/architect/prompt-log.md): every prompt agentd sends to an agent, to learn from.
-- System rules are long and repeat, so each distinct text is stored once, by its SHA-256; last_used keeps the rules a
-- prompt is using from being pruned under it.
CREATE TABLE agentd.agent_prompt_rules (
    hash      text        PRIMARY KEY,
    text      text        NOT NULL,
    last_used timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE agentd.agent_prompts (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source      text        NOT NULL CONSTRAINT ck_agent_prompt_source
        CHECK (source IN ('job', 'idea', 'chat', 'review', 'review-session', 'review-ask', 'review-fix', 'follow-up')),
    source_id   bigint      NOT NULL,
    step        text        NULL,
    session     uuid        NULL,
    resume      boolean     NOT NULL DEFAULT false,
    model       text        NULL,
    effort      text        NULL,
    profile     text        NULL,
    rules_hash  text        NULL REFERENCES agentd.agent_prompt_rules (hash),
    prompt      text        NOT NULL,
    reply       text        NULL,
    outcome     text        NULL CONSTRAINT ck_agent_prompt_outcome
        CHECK (outcome IN ('ok', 'error', 'usage-limit', 'timeout', 'cancelled')),
    error       text        NULL,
    started_at  timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz NULL
);
CREATE INDEX ix_agent_prompts_source ON agentd.agent_prompts (source, source_id, id DESC);
CREATE INDEX ix_agent_prompts_started ON agentd.agent_prompts (started_at);
