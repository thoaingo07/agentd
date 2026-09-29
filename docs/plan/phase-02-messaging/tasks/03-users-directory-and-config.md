# T2.3 — User directory v0 and messaging configuration

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.1 | S | Agentd.Host, Agentd.Application, Agentd.Infrastructure.Persistence |

## Goal
Load `Users` and `Messaging:*` from configuration with validation, and seed the user directory
tables. That gives the chat allowlist and identity mapping. Roles are stored but only enforced
in Phase 5.

## Files
- `src/Agentd.Application/Users/IUserDirectory.cs` — create: `FindByIdentityAsync(provider, externalId)`.
- `src/Agentd.Infrastructure.Persistence/Users/UserDirectory.cs` — create.
- `src/Agentd.Host/Options/UsersOptions.cs`, `MessagingOptions.cs` — create, with validators.
- `src/Agentd.Host/Startup/UserDirectorySeeder.cs` — create: an `IHostedService` that runs at startup.
- `src/Agentd.Host/appsettings.json` — modify: example sections (no secrets).

## Implementation
1. Configuration shape (see architecture README §3.1 and messaging-providers.md §6):
   ```jsonc
   "Users": [ { "Name": "tngo", "Email": "tngo@example.com", "Roles": ["Admin"],
                "Identities": { "Discord": "789...", "Telegram": "123456789" } } ],
   "Messaging": {
     "DefaultProviders": ["discord"],
     "Providers": {
       "Discord":  { "Enabled": true, "GuildId": "…", "ChannelId": "…" },
       "Telegram": { "Enabled": true, "ChatId": "-100…", "Mode": "LongPolling" }
     }
   }
   ```
   Bot tokens come only from `Agentd__Messaging__Providers__<Name>__BotToken`.
2. Validation at startup (`ValidateOnStart`):
   - every `DefaultProviders` entry is enabled;
   - an enabled provider has its token and required IDs;
   - user names are unique;
   - an identity is not mapped to two users.
3. Seeder (idempotent upsert):
   - config is the source of truth for `users` and `user_identities`;
   - users removed from config are marked `is_active = false`, never deleted, which keeps the audit
     trail.
4. `IUserDirectory` returns `AgentdUser(Id, Name, Roles, IsActive)`. Results are cached in memory
   and invalidated when the seeder runs.
5. **Provider selection per job:** `chat:<provider>` work item tags (for example `chat:telegram`)
   override `DefaultProviders`. Tags naming a provider that isn't enabled are ignored, with a
   warning event on the job. The per-repo kit override arrives in Phase 4. This resolver lives in
   Application as `ConversationTargetsResolver`.

## Tests
- Options validation: missing token, unknown default provider, duplicate identity → startup fails
  with a clear message.
- Seeder: first run inserts; second run is a no-op; removing a user marks them inactive.
- Resolver: default only; a tag override; an unknown tag ignored.

## Done when
- [ ] Starting with invalid messaging config fails fast with a readable error.
- [ ] `users` and `user_identities` reflect the config after startup.
- [ ] No token appears in logs or in `appsettings.json`.
