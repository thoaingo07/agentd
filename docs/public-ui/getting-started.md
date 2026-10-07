# Getting started

You need:
- **a Linux server** (or your workstation) to run agentd;
- **PostgreSQL**;
- **an Azure DevOps project**;
- **a Claude subscription** (Pro or Max) or an API key;
- optionally **Discord**, for chat.

## 1. Install agentd

Pick one.

### A. A release (recommended for a server)

```bash
curl -fsSL https://github.com/thoaingo07/agentd/releases/latest/download/install.sh | sh
```

- **What it does:** downloads the build for your machine (Linux x64 or arm64, macOS on Apple Silicon), checks it
  against the release's checksums, and installs it to `~/.local/bin/agentd`.
- **Options:** `AGENTD_VERSION=0.2.0` installs a specific version; `AGENTD_INSTALL_DIR=/opt/agentd/bin` installs
  elsewhere.
- **On Windows:** download `agentd-win-x64.exe` from the release page.

Agents build and test your repositories, so the server also needs **those repositories' toolchains** (for example the
.NET SDK, Node.js, Helm). `agentd doctor --repo <name>` tells you what's missing (step 7).

You also need **git**, **Node.js 24** and the **Claude Code CLI**:

```bash
npm install -g @anthropic-ai/claude-code
```

### B. Docker Compose

Download `compose.yaml` from the repository, then:

```bash
echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)" > .env    # optional: POSTGRES_PORT=55432, AGENTD_VERSION=0.2.0
docker compose up -d
docker compose logs -f agentd
```

- **What runs:** PostgreSQL, a one-time database migration, and agentd. The image already contains git, Node.js 24
  and the Claude Code CLI.
- **The UI listens on `127.0.0.1:7780` only,** on purpose (see [Reach the web UI](#8-reach-the-web-ui)). This
  setup is for Linux hosts.
- **To add your repositories' toolchains,** extend the image:

```dockerfile
FROM ghcr.io/thoaingo07/agentd:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends dotnet-sdk-10.0 && rm -rf /var/lib/apt/lists/*
USER agentd
```

In Docker, run the CLI commands below through the container, e.g. `docker compose exec -it agentd agentd doctor`.

### C. From source (development)

```bash
dotnet run --project src/Agentd.AppHost      # .NET Aspire: PostgreSQL, migrations, the web dev server and agentd
```

## Set up in the browser (the easy way)

Steps 2 to 6 below can also be done in a **setup wizard** in your browser. Each step has a **Test** button.

1. Start the daemon (`agentd daemon install && agentd daemon start`).
2. Until setup is finished, it logs a one-time link: `agentd daemon logs` shows
   `Open http://127.0.0.1:7780/setup?token=…`. `agentd setup-link` prints a new one.
3. On your laptop, open an SSH tunnel to the server (`ssh -L 7780:127.0.0.1:7780 <server>`), then open the link.
4. Go through **Database → Azure DevOps → Git access → Claude → Chat (optional) → Repositories → Review & finish**.
   Secrets you enter are stored encrypted and never shown again.
5. **Finish setup** checks everything again. It needs the database, Azure DevOps and Claude to pass. After that the
   link stops working.
6. Run `agentd daemon restart`, then open the web UI (step 8).

No browser at hand? `agentd init` asks the same questions in the terminal (and `agentd init --non-interactive` takes
them from options and environment variables, for scripts).

The sections below do the same by hand.

## 2. A database

With Docker Compose this is done for you. Otherwise run PostgreSQL 17 (a container is easiest), store its connection
string as a secret, and create the schema:

```bash
docker run -d --name agentd-pg --restart unless-stopped -p 127.0.0.1:5432:5432 \
  -e POSTGRES_PASSWORD="$(openssl rand -hex 24)" -v agentd-pg:/var/lib/postgresql/data postgres:17
printf 'Host=127.0.0.1;Port=5432;Username=postgres;Password=<the password>;Database=postgres' | agentd secrets set ConnectionStrings:agentd
agentd db migrate
```

## 3. Azure DevOps

Create `~/.agentd/config/agentd.json` (see [Configuration](configuration.md) for everything you can set):

```json
{
  "AzureDevOps": { "Organization": "myorg", "Project": "MyProject", "Auth": "Pat" }
}
```

Then store a personal access token as a secret. It needs the scopes **Work Items (Read & write)**, **Code (Read &
write)** and **Build (Read)**:

```bash
agentd secrets set AzureDevOps:Pat          # prompts for the value; it never goes on the command line
```

On a workstation where you're signed in with the Azure CLI, `"Auth": "AzCli"` uses `az login` instead.

## 4. Git access and repositories

agentd clones and manages repositories itself. For SSH remotes, give it its own key and add the public key to Azure
DevOps (**User settings → SSH public keys**):

```bash
ssh-keygen -t ed25519 -N '' -f ~/.agentd/ssh/id_ed25519
cat ~/.agentd/ssh/id_ed25519.pub
```

Register each repository by its clone URL. The default branch is detected:

```bash
agentd repo add git@ssh.dev.azure.com:v3/myorg/MyProject/my-repo --tag repo:my-repo
agentd repo list
```

A work item goes to a repository by its **`repo:<name>` tag**, or else by the longest matching **area path**
(`--area-path`). Admins can also do this from chat with `!repo add <url>`.

## 5. Claude

Either log in on the server:

```bash
claude auth login
```

or create a long-lived token on any machine that has a browser, and store it on the server:

```bash
claude setup-token                          # on your laptop: prints a token
agentd secrets set Claude:OAuthToken        # on the server: paste it at the prompt
```

## 6. Chat (optional): Discord

1. Create a Discord application with a bot and invite it to your server.
2. Give it permission to read and send messages, create threads, and manage threads.
3. Configure it:

```json
{
  "Messaging": { "Providers": { "Discord": { "Enabled": true, "GuildId": "<server id>", "ChannelId": "<channel id>" } } },
  "Users": [ { "Name": "alice", "Roles": ["Admin"], "Identities": { "Discord": "<alice's user id>" } } ]
}
```

```bash
agentd secrets set Messaging:Providers:Discord:BotToken
```

**Who can talk to agentd:** only the people listed in `Users`. Add `"AllowEveryone": true` to the Discord block to
accept everyone who can post in that channel; they get the Operator role, and Admins are still needed for `!repo add`.

## 7. Check everything, then start

```bash
agentd doctor --repo my-repo
```

Every line is ✅, ⚠️ or ❌ with a fix. `doctor` also sends one tiny prompt to Claude to prove the login works; use
`--skip-live` to skip that.

Run agentd as a service:

```bash
agentd daemon install && agentd daemon start
sudo loginctl enable-linger $USER          # once: keep it running after you log out, and start it at boot
agentd daemon logs -f
```

(Or run it in the foreground with `agentd daemon run`.)

## 8. Reach the web UI

The dashboard is at **http://127.0.0.1:7780** on the server, and only there: in its default mode agentd refuses
connections from anywhere but the machine itself. From your laptop, use an SSH tunnel:

```bash
ssh -L 7780:127.0.0.1:7780 my-server       # then open http://127.0.0.1:7780
```

To publish it properly, put it behind **Cloudflare Tunnel + Cloudflare Access**. See
[Configuration → Web access](configuration.md#web-access).

## 9. Your first job

1. In Azure DevOps, add the tag **`ai-workflow`** (and `repo:my-repo` if you have several repositories) to a work item.
2. Within a minute agentd tags it `ai-in-progress` and opens a thread in your Discord channel.
3. Follow [Working with jobs](jobs.md) from there.

Don't want to wait for the poll? Type `!run 1234` in the channel, or use **Run work item** on the dashboard.
