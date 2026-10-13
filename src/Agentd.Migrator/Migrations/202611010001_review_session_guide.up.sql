-- Guided reviews (docs/architect/review-sessions.md §2a): the reviewer's guide to the change, fitted to its files.
ALTER TABLE agentd.review_sessions ADD COLUMN guide jsonb NULL;   -- { overview, chapters: [{ title, why, files: [{ path, summary }] }], notes, suggestions }
