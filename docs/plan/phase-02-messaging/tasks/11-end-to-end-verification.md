# T2.11 — End-to-end verification and phase demo

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.1 – T2.10 | S | tests, docs |

## Goal
Prove the phase exit criteria with one scripted demo against the real sandbox (ADO + a Discord
test guild + a Telegram test supergroup), and keep an automated version with fakes in CI.

## Files
- `tests/Agentd.Application.Tests/EndToEnd/MessagingLoopTests.cs` — create: automated version (fakes + stub `claude`).
- `docs/plan/phase-02-messaging/demo.md` — create: the manual demo script and results.

## Implementation
1. **Automated (CI):** host the Application with fake ADO, fake providers (Discord-like and
   Telegram-like harnesses from T2.10) and a stub `claude` that asks one question with two options,
   then finishes. Assert:
   - both conversations are opened;
   - the question has options on both;
   - an option pressed on the Telegram fake resumes the job, with `--resume <same id>`;
   - the answer is mirrored into the Discord fake;
   - the PR link is posted to both;
   - an unknown user's reply is ignored;
   - `/cancel` cancels, and `/status` reports correctly.
2. **Outage case (CI):** the Discord fake fails for 60 s during the run → the job completes → the
   messages flush after recovery, in order.
3. **Manual demo** (`demo.md`) on the sandbox:
   1. tag an ambiguous work item with `ai-workflow` and `chat:discord`, `chat:telegram`;
   2. watch the question with buttons in both apps;
   3. answer in Telegram;
   4. check the mirror in Discord and the PR links;
   5. reply from an unmapped account → nothing happens;
   6. `/status` and `/cancel` on a second job;
   7. block Discord (firewall rule) during a third job → it completes → messages arrive after
      unblocking;
   8. hostile text from the agent (via a crafted work item) renders inert.

   Record the timestamps and screenshots in `demo.md`.

## Tests
- `MessagingLoopTests` (above), run in CI.

## Done when
- [ ] All phase exit criteria are demonstrated and recorded in `demo.md`.
- [ ] The automated end-to-end tests are green in GitHub Actions.
- [ ] Build review box ticked in `docs/plan/README.md`.
