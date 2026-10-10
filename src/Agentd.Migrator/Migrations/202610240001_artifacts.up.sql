-- Artifacts: HTML pages an agent publishes (a report, a diagram, an explainer), served by agentd at
-- /artifacts/<name>.html to anyone who can open the Web UI. <name> is a slug of the title plus a random hash.
-- Publishing again from the same job or chat with the same title replaces the page and keeps its link.
CREATE TABLE agentd.artifacts (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name       text        NOT NULL CONSTRAINT ux_artifacts_name UNIQUE,
    title      text        NOT NULL,
    html       text        NOT NULL,
    job_id     bigint      NULL,
    chat_id    bigint      NULL,
    author     text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_artifacts_source_title ON agentd.artifacts (job_id, chat_id, title) NULLS NOT DISTINCT;
