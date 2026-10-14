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

-- The Prompts page: newest first, filtered (all optional; p_outcome 'running' = not finished yet), a page of p_limit
-- below p_before (an id). The prompt is previewed; agent_prompt_get has it whole.
CREATE OR REPLACE FUNCTION agentd.agent_prompt_list(p_source text, p_source_id bigint, p_step text, p_model text, p_outcome text,
                                                    p_from timestamptz, p_before bigint, p_limit int)
RETURNS TABLE (id bigint, source text, source_id bigint, step text, session uuid, resume boolean, model text, effort text,
               profile text, outcome text, error text, started_at timestamptz, finished_at timestamptz, preview text,
               prompt_chars int, reply_chars int, has_rules boolean)
LANGUAGE sql
STABLE
AS $$
    SELECT p.id, p.source, p.source_id, p.step, p.session, p.resume, p.model, p.effort, p.profile, p.outcome, p.error,
           p.started_at, p.finished_at, left(p.prompt, 300), length(p.prompt), coalesce(length(p.reply), 0), p.rules_hash IS NOT NULL
      FROM agentd.agent_prompts AS p
     WHERE (p_source IS NULL OR p.source = p_source)
       AND (p_source_id IS NULL OR p.source_id = p_source_id)
       AND (p_step IS NULL OR p.step = p_step)
       AND (p_model IS NULL OR p.model = p_model)
       AND (p_outcome IS NULL OR (p_outcome = 'running' AND p.finished_at IS NULL) OR p.outcome = p_outcome)
       AND (p_from IS NULL OR p.started_at >= p_from)
       AND (p_before IS NULL OR p.id < p_before)
     ORDER BY p.id DESC
     LIMIT greatest(p_limit, 0)
$$;

-- One prompt, whole: what was sent, its rules and the reply.
CREATE OR REPLACE FUNCTION agentd.agent_prompt_get(p_id bigint)
RETURNS TABLE (id bigint, source text, source_id bigint, step text, session uuid, resume boolean, model text, effort text,
               profile text, outcome text, error text, started_at timestamptz, finished_at timestamptz, prompt text,
               reply text, rules text)
LANGUAGE sql
STABLE
AS $$
    SELECT p.id, p.source, p.source_id, p.step, p.session, p.resume, p.model, p.effort, p.profile, p.outcome, p.error,
           p.started_at, p.finished_at, p.prompt, p.reply, r.text
      FROM agentd.agent_prompts AS p
      LEFT JOIN agentd.agent_prompt_rules AS r ON r.hash = p.rules_hash
     WHERE p.id = p_id
$$;

-- A thread turn's transcript, written once when the turn ends: p_events is [{ "type", "payload", "at" }] in order.
CREATE OR REPLACE FUNCTION agentd.agent_prompt_events_add(p_prompt_id bigint, p_events jsonb)
RETURNS int
LANGUAGE sql
AS $$
    WITH added AS (
        INSERT INTO agentd.agent_prompt_events (prompt_id, type, payload, at)
        SELECT p_prompt_id, e ->> 'type', coalesce(e -> 'payload', '{}'::jsonb), coalesce((e ->> 'at')::timestamptz, now())
          FROM jsonb_array_elements(coalesce(p_events, '[]'::jsonb)) WITH ORDINALITY AS x(e, n)
         WHERE EXISTS (SELECT 1 FROM agentd.agent_prompts AS p WHERE p.id = p_prompt_id)
         ORDER BY x.n
        RETURNING 1)
    SELECT count(*)::int FROM added
$$;

-- A turn's transcript, in order: a thread turn's own events, or a job's agent events while the turn ran.
CREATE OR REPLACE FUNCTION agentd.agent_prompt_transcript(p_prompt_id bigint, p_limit int)
RETURNS TABLE (type text, payload jsonb, at timestamptz)
LANGUAGE sql STABLE
AS $$
    SELECT t.type, t.payload, t.at FROM (
        SELECT e.type, e.payload, e.at, e.id AS ord
          FROM agentd.agent_prompt_events AS e
         WHERE e.prompt_id = p_prompt_id
        UNION ALL
        SELECT ev.type, ev.payload, ev.ts, ev.seq
          FROM agentd.agent_prompts AS p
          JOIN agentd.events AS ev ON ev.job_id = p.source_id
         WHERE p.id = p_prompt_id AND p.source = 'job' AND ev.type LIKE 'agent.%'
           AND ev.ts >= p.started_at AND ev.ts <= coalesce(p.finished_at, now())
    ) AS t
     ORDER BY t.ord
     LIMIT greatest(1, least(coalesce(p_limit, 5000), 20000))
$$;
