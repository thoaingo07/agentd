-- The PR Monitor is turned off for now: its routines go, its tables (agentd.pr_watches, agentd.pr_watch_messages) and
-- their rows stay, so watching can come back without losing the history.
DO $$
DECLARE
    r record;
BEGIN
    FOR r IN SELECT p.oid::regprocedure AS sig
             FROM pg_proc AS p JOIN pg_namespace AS n ON n.oid = p.pronamespace
             WHERE n.nspname = 'agentd' AND p.proname LIKE 'pr\_watch\_%'
    LOOP
        EXECUTE format('DROP FUNCTION %s', r.sig);
    END LOOP;
END
$$;
