-- Rule keys the old shell splitter made from quoted text and loop syntax (e.g. Bash(probe\:*), Bash(do for:*),
-- Bash(]*':*)): they never match a real command again, so they only clutter Settings. A real key is a command word
-- and an optional plain subcommand, and the command isn't shell syntax.
DELETE FROM agentd.permission_rules
 WHERE rule_key LIKE 'Bash(%:*)'
   AND (substring(rule_key FROM '^Bash\((.*):\*\)$') !~ '^[A-Za-z0-9_./+@-]+( [a-z][a-z0-9-]*)?$'
        OR split_part(substring(rule_key FROM '^Bash\((.*):\*\)$'), ' ', 1)
           IN ('do', 'done', 'then', 'else', 'elif', 'fi', 'for', 'in', 'if', 'while', 'until', 'case', 'esac'));

-- Requests left open by jobs that have finished can never be answered.
UPDATE agentd.permission_requests AS r
   SET status = 'expired', decided_by = 'cleanup', decided_at = now()
  FROM agentd.jobs AS j
 WHERE r.job_id = j.id AND r.status = 'pending' AND j.state IN ('Done', 'Failed', 'Cancelled');
