-- History pages sort jobs by updated_at (for a finished job: when it finished). With a state filter the
-- planner still walks this index and filters (checked with EXPLAIN on 10k jobs: 0.05 ms for a page).
CREATE INDEX ix_jobs_history ON agentd.jobs (updated_at DESC, id DESC);
