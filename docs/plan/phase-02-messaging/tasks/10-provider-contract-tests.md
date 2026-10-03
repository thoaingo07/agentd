# T2.10 — Provider contract test suite

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.8, T2.9 | M | tests/Agentd.Infrastructure.Tests |

## Goal
One reusable test suite that every messaging provider must pass. It proves that Discord and
Telegram behave the same through the port, and it becomes the entry ticket for future providers
(Slack, Teams).

## Files
- `tests/Agentd.Infrastructure.Tests/Messaging/Contract/MessagingProviderContractTests.cs` — create:
  an abstract generic base `MessagingProviderContractTests<TProvider>`.
- `tests/Agentd.Infrastructure.Tests/Messaging/Contract/IProviderTestHarness.cs` — create.
- `tests/.../Discord/DiscordContractTests.cs`, `tests/.../Telegram/TelegramContractTests.cs` — create.
- `tests/.../Messaging/Contract/fixtures/hostile-inputs.txt` — create.

## Implementation
1. **Harness abstraction:** each provider supplies a harness that
   - builds the provider against a **fake transport** (a recording HTTP handler / gateway stub);
   - exposes what was "sent" in the platform's wire format;
   - can inject platform-native inbound events.

   No network access is used in CI.
2. **Contract cases** (each is a test method in the base class):
   - `OpenConversation_returns_ref_and_link`;
   - `Send_respects_MaxMessageLength_after_rendering` (with the chunker from T2.4);
   - `Hostile_text_is_inert`: for every line in `hostile-inputs.txt`, the rendered wire text
     contains no active mention or markup (`@everyone`, raw HTML tags, markdown links to
     `javascript:`, nested fences);
   - `Options_round_trip`: send with options → inject a press of option 2 → the sink receives
     `SelectedOptionId = option2.Id`;
   - `Inbound_from_bot_is_ignored`;
   - `Inbound_duplicate_is_idempotent` (injected twice → the sink sees the same
     `ExternalMessageId`; the Application layer dedupes);
   - `Commands_normalize` (`status`, `cancel`, `run 1234`);
   - `Edit_updates_existing_message` (when `SupportsEditing`);
   - `Health_reports_down_when_transport_fails`.
3. Capability-dependent cases are skipped explicitly when the capability is false, and the skip is
   reported.
4. `hostile-inputs.txt` includes: `@everyone`, `@here`, `<@123>`, `<b>x</b>`, `<a href="javascript:alert(1)">`,
   ````` ```nested``` `````, `[x](javascript:alert(1))`, `&lt;script&gt;`, a 10,000-character line,
   RTL override characters and zero-width joiners.

## Tests
This task *is* tests. Both provider subclasses run in the normal `dotnet test` run.

## As built
- **The base class is non-generic:** `MessagingProviderContractTests` with an abstract
  `CreateHarness()`. A provider's `[TestClass]` derives from it and inherits the 9 contract tests.
  Discord: `DiscordContractTests`.
- **The harness** (`IProviderTestHarness`) delivers native messages through the provider's real
  inbound path (for Discord, the poller against the fake REST transport). Each platform supplies its
  own `ActiveMarkup` rules and per-request checks (Discord: every message carries an empty
  `allowed_mentions`).
- **`hostile-inputs.txt`:**
  - mass, user, role and channel mentions; HTML and `javascript:` / `data:` links;
  - nested and inline fences, headings, quotes, entities;
  - RTL overrides and zero-width joiners;
  - a 10,000-character line, and 600 `@everyone`s.
- **The suite found two real Discord bugs, both fixed here:**
  1. Neutralizing hundreds of mentions grew a message past 2,000 characters (2,118). The provider now
     re-splits rendered text that exceeds the limit (an edit is cut to the limit).
  2. A one-line code span (``` ``` @everyone ``` @here ```) was treated as the start of a code block,
     which left the rest of the message unneutralized. Only a line with an odd number of fences opens
     or closes a block now.
- **Wiring proved:** disabling mass-mention neutralization makes `Hostile_text_is_inert` fail.
- **Telegram** is deferred with T2.9; its harness will plug into the same base class.

## Done when
- [x] Every built provider (Discord) passes every applicable contract case. Telegram is deferred.
- [x] Breaking the renderer makes the suite fail, which proves it is wired correctly.
- [x] `docs/architect/messaging-providers.md` §9 links to the suite.
