-- 202609300002 repositories: registered by URL (any repo); agentd manages their clones.
CREATE TABLE agentd.repositories (
    name              text PRIMARY KEY,
    remote_url        text        NOT NULL,
    provider          text        NOT NULL DEFAULT 'azure-devops',
    organization      text        NOT NULL,
    project           text        NOT NULL,
    repo              text        NOT NULL,
    base_branch       text        NOT NULL,
    match_tag         text        NULL,
    match_area_paths  text[]      NOT NULL DEFAULT '{}',
    enabled           boolean     NOT NULL DEFAULT true,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    version           bigint      NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_repositories_remote ON agentd.repositories (lower(remote_url));
