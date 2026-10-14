DELETE FROM agentd.agent_prompts WHERE source = 'incident';
ALTER TABLE agentd.agent_prompts DROP CONSTRAINT ck_agent_prompt_source;
ALTER TABLE agentd.agent_prompts ADD CONSTRAINT ck_agent_prompt_source
    CHECK (source IN ('job', 'idea', 'chat', 'review', 'review-session', 'review-ask', 'review-fix', 'follow-up'));
ALTER TABLE agentd.incidents DROP COLUMN analysis, DROP COLUMN session, DROP COLUMN analyzed_at;
