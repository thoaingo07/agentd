# Discord reference

This covers the Discord messaging provider (`Infrastructure.Messaging.Discord`). See
[messaging-providers.md](../messaging-providers.md) for the provider contract.

## Bot setup

1. Create an application at https://discord.com/developers/applications, then add a **Bot**.
2. Enable the **Message Content Intent** (a privileged intent). The bot has to read developer replies.
3. Invite the bot with the scopes `bot` and `applications.commands`, and these permissions:
   View Channel, Send Messages, Create Public Threads, Send Messages in Threads,
   Read Message History, Embed Links, Attach Files.
4. Store the token in the `Agentd__Messaging__Providers__Discord__BotToken` env var (or user-secrets in dev).

agentd uses **plain REST, no Gateway** (decided 2026-10-03). Replies are read by polling
`GET /channels/{id}/messages?after={lastId}`, which still requires the **Message Content** intent to
see message text. Gateway intents (`Guilds`, `GuildMessages`, `MessageContent`) only matter if a
Gateway listener is added later.

## One thread per work item

- Post a starter message in the parent channel, then start a thread from it:
  `POST /channels/{channel_id}/messages/{message_id}/threads` (Discord.Net: `textChannel.CreateThreadAsync(name, ThreadType.PublicThread, message: starter)`).
- Thread names can be at most 100 characters, for example `WI-1234 · Fix login redirect`.
- Threads auto-archive after inactivity (`auto_archive_duration`: 60, 1440, 4320 or 10080 minutes).
  Posting to an archived thread unarchives it, so the bot can always reply.
- Store `thread.id` in the job record. Inbound `MessageReceived` events whose `channelId` is that
  thread ID route to the job.

## Message limits

- Message content can be at most 2000 characters, so split long output or attach it as a file
  (for example a diff or log).
- Rate limits apply per route. Batch progress updates, e.g. at most one every 10 seconds per thread.

## Commands

Slash commands (registered per guild, so they update instantly):

| Command | Where | Action |
|---|---|---|
| `/agentd list` | channel | all jobs and their states |
| `/agentd run id:<n>` | channel | start a work item immediately |
| `/agentd status` | thread | the state of this job |
| `/agentd cancel` | thread | stop the job and release the work item |
| `/agentd retry` | thread | re-queue a failed job |
| `/agentd logs` | thread | attach the transcript |

## Links

- Discord.Net docs: https://docs.discordnet.dev
- Threads: https://discord.com/developers/docs/topics/threads
- Gateway intents: https://discord.com/developers/docs/topics/gateway#gateway-intents
