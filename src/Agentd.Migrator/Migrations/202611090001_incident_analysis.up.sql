-- Investigating incidents (docs/architect/incident-monitor.md §3): a read-only agent's analysis, kept with the incident
-- with its session (follow-up questions resume it); the prompt log knows the turns as source 'incident', and the
-- Inbox takes incidents' messages (the constraint from 2026_11_03_0001 didn't list 'incident').
ALTER TABLE agentd.incidents
    ADD COLUMN analysis    text        NULL,
    ADD COLUMN session     uuid        NULL,
    ADD COLUMN analyzed_at timestamptz NULL;

ALTER TABLE agentd.agent_prompts DROP CONSTRAINT ck_agent_prompt_source;
ALTER TABLE agentd.agent_prompts ADD CONSTRAINT ck_agent_prompt_source
    CHECK (source IN ('job', 'idea', 'chat', 'review', 'review-session', 'review-ask', 'review-fix', 'follow-up', 'incident'));

ALTER TABLE agentd.inbox_items DROP CONSTRAINT ck_inbox_source;
ALTER TABLE agentd.inbox_items ADD CONSTRAINT ck_inbox_source CHECK (source IN ('job', 'idea', 'review', 'chat', 'watch', 'incident'));
