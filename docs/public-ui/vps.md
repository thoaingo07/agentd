# Run agentd on a VPS

A step-by-step setup on a fresh **Ubuntu 24.04** server:
- agentd runs as its own unprivileged user;
- the web UI stays private;
- the database is backed up.

There are two ways to install, the **binary with a systemd service** (A) or **Docker Compose** (B). Pick one.

> **Before the first release.** The install script downloads from GitHub Releases, which start with v0.1.0. Until
> then, build the binary on a development machine (`build/publish.sh linux-x64`, which produces `artifacts/linux-x64/agentd`)
> and copy it to the server's `~/.local/bin/agentd`.

## 1. Size the server

- **4 vCPU, 16 GB RAM** runs 2–3 jobs at once (`Scheduler:MaxConcurrent`). Each job is one `claude` process plus
  your repository's builds and tests, so heavy test suites need more.
- **Disk:** 40 GB or more. Each repository is cloned once, and each running job adds a worktree. Finished
  worktrees are removed after a day.
- **Outbound traffic** goes only to Azure DevOps, Anthropic and Discord (if you use it). Nothing has to be reachable
  from the internet.

## 2. Prepare the server (as your admin user)

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y git curl ca-certificates ufw

# A firewall with only SSH open.
sudo ufw allow OpenSSH && sudo ufw enable

# The user agentd runs as: no password, no sudo. Agents inherit its permissions, nothing more.
sudo adduser --disabled-password --gecos "" agentd
sudo mkdir -p /home/agentd/.ssh && sudo cp ~/.ssh/authorized_keys /home/agentd/.ssh/
sudo chown -R agentd:agentd /home/agentd/.ssh && sudo chmod 700 /home/agentd/.ssh

# Keep its services running after logout, and start them at boot.
sudo loginctl enable-linger agentd
```

**Tools the agents need** (path A; the Docker image already has git, Node and Claude Code):

```bash
# Node 24 and Claude Code
curl -fsSL https://deb.nodesource.com/setup_24.x | sudo -E bash -
sudo apt install -y nodejs
sudo npm install -g @anthropic-ai/claude-code

# Whatever your repositories build with, for example .NET:
sudo apt install -y dotnet-sdk-10.0
```

Later, `agentd doctor --repo <name>` lists any toolchain a repository needs that's missing.

**Optional: Tailscale.** With [Tailscale](https://tailscale.com/download/linux) on the server (`sudo tailscale up`)
you can SSH over your tailnet and close public SSH (`sudo ufw delete allow OpenSSH`). Do that only after you've checked
that Tailscale SSH works.

## 3. A database (path A)

PostgreSQL from Ubuntu, listening on localhost only (the default):

```bash
sudo apt install -y postgresql
sudo -u postgres createuser --pwprompt agentd      # choose a password; you'll paste it in step 5
sudo -u postgres createdb --owner agentd agentd
```

The connection string is `Host=127.0.0.1;Port=5432;Username=agentd;Password=<it>;Database=agentd`.

## 4A. Install agentd with systemd

Log in **as `agentd`** over SSH (`ssh agentd@<server>`). `systemctl --user` needs a real login session, so don't use `sudo -iu`.

```bash
curl -fsSL https://github.com/thoaingo07/agentd/releases/latest/download/install.sh | sh   # → ~/.local/bin/agentd
agentd version
agentd daemon install && agentd daemon start
agentd daemon logs | grep setup?token     # the one-time setup link
```

The script checks the binary against the release's `sha256sums.txt`. If `agentd` isn't found, open a new login
shell: Ubuntu adds `~/.local/bin` to `PATH` once it exists.

Continue with [step 5](#5-set-it-up).

## 4B. Install agentd with Docker Compose

As your admin user, [install Docker Engine](https://docs.docker.com/engine/install/ubuntu/). Then:

```bash
mkdir -p ~/agentd && cd ~/agentd
curl -fsSLO https://raw.githubusercontent.com/thoaingo07/agentd/main/compose.yaml
echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)" > .env && chmod 600 .env   # optional: POSTGRES_PORT, AGENTD_VERSION
docker compose up -d
docker compose logs agentd | grep setup?token
```

Compose runs:
- PostgreSQL 17;
- a one-shot `migrate`;
- the daemon, as user `agentd` (uid 10001), with `~/.agentd` in the `agentd-home` volume.

Everything uses the host network on **127.0.0.1 only**, and the database password stays in `.env`.

If your repositories need more toolchains, extend the image ([Configuration](configuration.md) has the rest):

```dockerfile
FROM ghcr.io/thoaingo07/agentd:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends dotnet-sdk-10.0 && rm -rf /var/lib/apt/lists/*
USER agentd
```

In the steps below, run `agentd …` commands as `docker compose exec -it agentd agentd …`.

## 5. Set it up

1. On your laptop, open a tunnel: `ssh -N -L 7780:127.0.0.1:7780 agentd@<server>` (over Tailscale, use the server's
   tailnet name).
2. Open the setup link from step 4 in your browser. `agentd setup-link` prints a new one at any time.
3. Go through the wizard ([Getting started](getting-started.md#set-up-in-the-browser-the-easy-way)):
   1. **Database:** paste the connection string, Test, then create the schema. On Docker it already shows
      "from the environment" and the schema is migrated, so Test is all you need.
   2. **Azure DevOps:** organization, project, and a PAT.
   3. **Git access:** generate agentd's SSH key and add it in Azure DevOps.
   4. **Claude:** run `claude setup-token` on your laptop and paste the token.
   5. **Chat** (optional).
   6. **Repositories.**
   7. **Review & finish.**
4. `agentd daemon restart` (Docker: `docker compose restart agentd`), then `agentd doctor`: everything should be ✅.

No browser? Use `agentd init` in the SSH session instead. It asks the same questions.

## 6. Reach the web UI every day

- **SSH tunnel** (as in step 5): `ssh -N -L 7780:127.0.0.1:7780 agentd@<server>`, then http://127.0.0.1:7780.
- **Cloudflare Tunnel + Access** for a real address with a login: see [Configuration → Web access](configuration.md#web-access).
  Never publish `/mcp`.

agentd only ever listens on 127.0.0.1. Don't put a plain reverse proxy in front of it: without Cloudflare Access, it
refuses proxied requests on purpose.

## 7. Back up

What you need to restore:
- the **database**;
- **`~/.agentd/config`** (settings and the encrypted secrets);
- **`~/.agentd/keys`**: without it the secrets can't be decrypted;
- **`~/.agentd/ssh`** (agentd's git key).

Clones and worktrees are recreated.

**Path A**, a nightly cron job as `agentd`. Keep the password out of the command line with `~/.pgpass`:

```bash
echo "127.0.0.1:5432:agentd:agentd:<password>" > ~/.pgpass && chmod 600 ~/.pgpass
mkdir -p ~/backups && chmod 700 ~/backups
crontab -e
# 30 3 * * * pg_dump -h 127.0.0.1 -U agentd -Fc agentd > ~/backups/agentd-$(date +\%F).dump && tar -C ~ -czf ~/backups/agentd-home-$(date +\%F).tgz .agentd/config .agentd/keys .agentd/ssh && find ~/backups -mtime +14 -delete
```

**Path B:**

```bash
docker compose exec -T postgres pg_dump -U postgres -Fc postgres > ~/backups/agentd-$(date +%F).dump
docker run --rm -v agentd_agentd-home:/h -v ~/backups:/b alpine tar -C /h -czf /b/agentd-home-$(date +%F).tgz config keys ssh
```

Compose names the volume `<folder>_agentd-home`. Check yours with `docker volume ls`.

**Copy the backups off the server**, and keep them private. The home archive holds the key ring, so together with
`secrets.json` it can decrypt your tokens.

## 8. Restore on a new server

1. Do steps 1–4, but **don't run the setup wizard**.
2. Stop the daemon: `agentd daemon stop` (Docker: `docker compose stop agentd`).
3. Restore the files: `tar -C ~ -xzf agentd-home-<date>.tgz`. Then `chmod 700 ~/.agentd ~/.agentd/*` and `chmod 600 ~/.agentd/config/* ~/.agentd/ssh/id_ed25519`.
   Docker: `docker run --rm -v agentd_agentd-home:/h -v ~/backups:/b alpine sh -c 'tar -C /h -xzf /b/agentd-home-<date>.tgz && chown -R 10001:10001 /h'`.
4. Restore the database: `pg_restore -h 127.0.0.1 -U agentd -d agentd --clean --if-exists agentd-<date>.dump`
   (Docker: `docker compose exec -T postgres pg_restore -U postgres -d postgres --clean --if-exists < agentd-<date>.dump`).
5. `agentd db migrate`, then `agentd daemon start` and `agentd doctor`.

Jobs that were running resume on this server. Restore onto one server only: two daemons on the same database would
both claim work.

## 9. Update

- **Path A:** `agentd update`. It downloads the release, checks its checksum, keeps the old binary as
  `agentd.previous`, migrates the database and restarts the service. `agentd update --check` only looks;
  `--channel beta` follows pre-releases.
- **Path B:** set `AGENTD_VERSION` in `.env` (or keep `latest`), then `docker compose pull && docker compose up -d`.
  The `migrate` service runs first.

## Checklist

- [ ] agentd runs as `agentd`, which has no sudo, and lingering is on.
- [ ] Only SSH is open in the firewall (or nothing, with Tailscale).
- [ ] The web UI is reached through an SSH tunnel or Cloudflare Access, never a plain proxy.
- [ ] `agentd doctor` is all ✅.
- [ ] Backups run nightly and are copied off the server.
