# T1b.2 — Encrypted secret store

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | — | M | Host (configuration, CLI) |

## Goal
Store secrets (PATs, bot tokens, Claude tokens, the database password) encrypted in
`~/.agentd/config/secrets.json`, load them as a configuration source, and manage them with
`agentd secrets set|list|remove`. They never reach an agent's environment.

## Files
- `src/Agentd.Host/Configuration/SecretStore.cs`: create. Read/write `secrets.json`: `{ "secrets": { "<key>": { "value": "<protected>", "updatedAt": "…", "updatedBy": "…" } } }`.
  Values are protected with ASP.NET Core Data Protection (`DataProtectionProvider.Create(keys, "agentd")`, purpose
  `agentd.secrets.v1`); the file is written atomically (temp + rename) with mode 0600.
- `src/Agentd.Host/Configuration/SecretsConfigurationSource.cs`: create. Decrypts every secret into configuration.
  A key `AzureDevOps:Pat` becomes `Agentd:AzureDevOps:Pat`; a key starting with `ConnectionStrings:` is used as-is.
- `src/Agentd.Host/Configuration/AgentdConfiguration.cs`: modify. The secrets source sits above `agentd.json` and
  below `AGENTD_*` variables (containers can still override with the environment).
- `src/Agentd.Host/Cli/Commands/SecretsCommand.cs`: create. `set <key>` (value from a no-echo prompt or piped stdin,
  **never from argv**), `list` (names and dates only), `remove <key>`.
- `src/Agentd.Host/DaemonHost.cs`: modify. The daemon's Data Protection uses the same key ring and application name.

## Implementation
1. One key ring for cookies and secrets: `~/.agentd/keys` (0700), application name `agentd`.
2. Key names: letters, digits, `:`, `_`, `-`, `.`; at most 200 characters. Values: at most 64 KiB.
3. A secret that doesn't decrypt (lost key ring) is skipped with one warning naming the key, never the value; `doctor`
   reports it (T1b.4).
4. Nothing new is needed for agents: secrets live in configuration, not the process environment, and `SafeEnvironment`
   already strips `AGENTD_`/`ConnectionStrings__` variables.

## Tests
- Round trip set → load as configuration → remove; the file never contains the plain value; mode 0600.
- Layering: `agentd.json` < secrets < `AGENTD_*`.
- A tampered or foreign-key value is skipped with a warning that doesn't contain the value.
- `secrets set` refuses a value given on the command line; reads piped stdin.
- An agent's environment never contains a secret value (runner test with a secret configured).

## Done when
- [x] `agentd secrets set|list|remove` work; values are encrypted at rest.
- [x] The daemon and the CLI read secrets as configuration (e.g. `AzureDevOps:Pat` with `Auth=Pat`).
- [x] No secret value in logs, `list` output, or agent environments (tests).

## As built (2026-10-05)
- `src/Agentd.Host/Configuration/SecretStore.cs`, `SecretsConfigurationSource.cs`, `Cli/Commands/SecretsCommand.cs`,
  `Cli/ConsoleSecret.cs` (piped stdin or a no-echo prompt). `CliContext` gained the config home and the secret reader,
  so tests run the real verbs against a temp home.
- Agents: secrets are configuration only, never process environment variables, and `SafeEnvironment` already strips
  `AGENTD_*` and `ConnectionStrings__*`. So no runner change was needed, and no extra runner test was added.

