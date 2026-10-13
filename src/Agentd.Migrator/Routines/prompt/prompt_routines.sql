-- The prompt log (docs/architect/prompt-log.md).

-- Records a prompt as it's sent: its rules once per distinct text (parallel callers with the same rules share one row).
-- Touching last_used locks the rules row, so a prune running at the same time waits and then keeps it.
CREATE OR REPLACE FUNCTION agentd.agent_prompt_start(p_source text, p_source_id bigint, p_step text, p_session uuid, p_resume boolean,
                                                     p_model text, p_effort text, p_profile text, p_rules text, p_prompt text)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    v_hash text := CASE WHEN p_rules IS NULL THEN NULL ELSE encode(sha256(convert_to(p_rules, 'UTF8')), 'hex') END;
    v_id   bigint;
BEGIN
    IF v_hash IS NOT NULL THEN
        INSERT INTO agentd.agent_prompt_rules (hash, text) VALUES (v_hash, p_rules)
        ON CONFLICT (hash) DO UPDATE SET last_used = now();
    END IF;

    INSERT INTO agentd.agent_prompts (source, source_id, step, session, resume, model, effort, profile, rules_hash, prompt)
    VALUES (p_source, p_source_id, p_step, p_session, p_resume, p_model, p_effort, p_profile, v_hash, p_prompt)
    RETURNING id INTO v_id;
    RETURN v_id;
END
$$;

-- How the prompt's turn ended (once: a second finish changes nothing).
CREATE OR REPLACE FUNCTION agentd.agent_prompt_finish(p_id bigint, p_outcome text, p_error text, p_reply text)
RETURNS boolean
LANGUAGE sql
AS $$
    WITH updated AS (
        UPDATE agentd.agent_prompts
           SET outcome = p_outcome, error = p_error, reply = p_reply, finished_at = now()
         WHERE id = p_id AND finished_at IS NULL
        RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM updated)
$$;

-- Retention: prompts sent before p_before, then rules no prompt uses and none has used since. Returns the prompts deleted.
CREATE OR REPLACE FUNCTION agentd.agent_prompt_delete_before(p_before timestamptz)
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    v_count int;
BEGIN
    WITH deleted AS (DELETE FROM agentd.agent_prompts WHERE started_at < p_before RETURNING 1)
    SELECT count(*)::int INTO v_count FROM deleted;

    DELETE FROM agentd.agent_prompt_rules AS r
     WHERE r.last_used < p_before
       AND NOT EXISTS (SELECT 1 FROM agentd.agent_prompts AS p WHERE p.rules_hash = r.hash);
    RETURN v_count;
END
$$;
