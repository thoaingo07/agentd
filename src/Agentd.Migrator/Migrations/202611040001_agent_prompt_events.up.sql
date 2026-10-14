-- A thread turn's transcript in the prompt log (docs/architect/prompt-log.md): every event of the agent's run (text,
-- tool calls and results), like a job's events. A job's turn reads its events from agentd.events instead.
CREATE TABLE agentd.agent_prompt_events (
    id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    prompt_id bigint      NOT NULL REFERENCES agentd.agent_prompts (id) ON DELETE CASCADE,
    type      text        NOT NULL,
    payload   jsonb       NOT NULL,
    at        timestamptz NOT NULL
);
CREATE INDEX ix_agent_prompt_events_prompt ON agentd.agent_prompt_events (prompt_id, id);
