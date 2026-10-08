DROP FUNCTION IF EXISTS agentd.job_session_get_or_create(bigint, text, uuid);
DROP FUNCTION IF EXISTS agentd.job_plan_save(bigint, text, timestamptz);
DROP FUNCTION IF EXISTS agentd.job_plan_get(bigint);
DROP TABLE agentd.job_plans;
DROP TABLE agentd.job_sessions;
