-- Messages in a 🚨 thread find their incident (incident_find_by_thread): every inbound thread message looks.
CREATE INDEX ix_incidents_thread ON agentd.incidents (provider, thread_id) WHERE thread_id IS NOT NULL;
