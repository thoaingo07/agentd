# Command line

`agentd` is one executable: with no arguments, or with `daemon run`, it's the daemon; everything else is a command
for the operator. `agentd --help` and `agentd <command> --help` show every option.

## The daemon

| Command | What it does |
|---|---|
| `agentd daemon run` | run in the foreground (what systemd and Docker run) |
| `agentd daemon install` | install it as a **systemd user service** and enable it; prints the `loginctl enable-linger` hint when needed |
| `agentd daemon start` · `stop` · `restart` · `status` | control the service (stopping gives running agents a minute to finish) |
| `agentd daemon logs [-f] [-n 200]` | the service's log |
| `agentd daemon uninstall` | remove the service; `~/.agentd` is kept |

## Jobs and repositories

| Command | What it does |
|---|---|
| `agentd status [--all]` | active jobs (`--all` adds the ones finished in the last 24 hours). With the daemon running it also shows each job's phase and what it's doing right now; otherwise it reads the database and says so |
| `agentd run <work item id> [--repo name]` | queue a work item now, even without the tag (`--repo` overrides the tag/area-path match). A running daemon starts it at once |
| `agentd repo add <clone url> [--name n] [--base b] [--tag repo:x] [--area-path p]…` | register a repository and clone it (the base defaults to the remote's default branch, the tag to `repo:<name>`; `--area-path` can repeat) |
| `agentd repo list` · `agentd repo remove <name>` | list, or unregister (job history is kept) |

## Setup and maintenance

| Command | What it does |
|---|---|
| `agentd doctor [--repo <name>] [--skip-live]` | check everything, with ✅ / ⚠️ / ❌ and a fix for each problem; `--repo` also checks the toolchains that repository needs |
| `agentd db migrate` | create or update the database schema (the daemon never does it itself) |
| `agentd secrets set <name>` | store a secret, encrypted; the value comes from a hidden prompt or piped stdin, **never** the command line |
| `agentd secrets list` · `agentd secrets remove <name>` | names and dates only, never values |
| `agentd update [--channel stable\|beta] [--check] [--to 0.2.0]` | move to a newer release: checksum-verified, the old binary kept as `agentd.previous`, then the database migrated and the service restarted |
| `agentd version` | version, platform and commit |
| `agentd init [--non-interactive …]` | set the server up in the terminal: the wizard's steps, then a review and Finish. Enter keeps a saved value; secrets are typed hidden. `--non-interactive` takes `--organization`, `--project`, `--auth`, `--repo` (repeatable), `--generate-ssh-key`, `--discord-guild`/`--discord-channel`/`--user-name`/`--discord-user`, and secrets **only** from `AGENTD_INIT_DATABASE`, `AGENTD_INIT_ADO_PAT`, `AGENTD_INIT_CLAUDE_TOKEN`, `AGENTD_INIT_DISCORD_TOKEN`; anything missing is listed (exit 2) |
| `agentd setup-link` | before setup is complete: print a new one-time link to the setup page (the previous link stops working) |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | an error |
| 2 | wrong usage (the message says how to run it) |
| 3 | not found (e.g. the work item or repository) |
| 4 | a conflict (e.g. the work item already has an active job) |
| 5 | `doctor` found a failure |
