-- The work item view reads every job, and every outbound message, of one work item.
CREATE INDEX ix_jobs_work_item ON agentd.jobs (work_item_id, created_at);
CREATE INDEX ix_outbound_job ON agentd.outbound_messages (job_id, created_at);
