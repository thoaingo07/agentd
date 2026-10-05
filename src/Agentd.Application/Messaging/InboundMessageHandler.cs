using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Users;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Domain.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Messaging;

/// <summary>Inbound idempotency (PostgreSQL routines).</summary>
public interface IInboundLog
{
    /// <summary>Claims a provider message; false when it was already handled.</summary>
    Task<bool> TryRecordAsync(ProviderKey provider, string externalMessageId, DateTimeOffset receivedAt, CancellationToken cancellationToken);

    Task SetOutcomeAsync(ProviderKey provider, string externalMessageId, string outcome, JobId? jobId, UserId? userId, CancellationToken cancellationToken);

    /// <summary>Releases a claim after a failure, so a redelivery is handled again.</summary>
    Task ForgetAsync(ProviderKey provider, string externalMessageId, CancellationToken cancellationToken);
}

/// <summary>What became of an inbound message (also stored as <c>inbound_messages.outcome</c>).</summary>
public sealed record InboundOutcome(string Code, JobId? JobId = null);

/// <summary>
/// Handles every message from every provider: dedupe → authorize (user directory) → route to the job
/// by its conversation → deliver as a developer message. Strangers get no reply, so the bot isn't
/// confirmed to them, unless the provider has <see cref="MessagingProviderSettings.AllowEveryone"/> on. Commands go to <see cref="ChatCommands"/>. Mirroring to the job's other
/// conversations comes from the job's events.
/// </summary>
public sealed partial class InboundMessageHandler(
    IInboundLog log,
    IUserDirectory users,
    IConversationStore conversations,
    ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome> submit,
    ChatCommands commands,
    ICommandHandler<AnswerCloseOut, bool> closeOut,
    IOutbox outbox,
    IJobRepository jobs,
    JobActivity activity,
    Domain.Common.IClock clock,
    ILogger<InboundMessageHandler> logger,
    IOptionsMonitor<MessagingOptions>? messaging = null,
    ICommandHandler<Permissions.PermissionAnswer, bool>? permissions = null,
    Ideas.IdeaService? ideas = null,
    Ideas.IIdeaStore? ideaStore = null) : IInboundMessageSink
{
    /// <summary>The role a stranger gets on a provider that allows everyone (enforced from Phase 5).</summary>
    public const string GuestRole = "Operator";

    public Task HandleAsync(InboundMessage message, CancellationToken cancellationToken) => ProcessAsync(message, cancellationToken);

    public async Task<InboundOutcome> ProcessAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!await log.TryRecordAsync(message.Provider, message.ExternalMessageId, message.SentAt, cancellationToken).ConfigureAwait(false))
        {
            return new InboundOutcome("duplicate");
        }

        try
        {
            var user = await users.FindByIdentityAsync(message.Provider, message.ExternalUserId, cancellationToken).ConfigureAwait(false);
            var actor = user ?? Guest(message);   // a listed but inactive user is never replaced by a guest
            var outcome = actor is { IsActive: true }
                ? await RouteAsync(message, actor, cancellationToken).ConfigureAwait(false)
                : Unknown(message);
            await log.SetOutcomeAsync(message.Provider, message.ExternalMessageId, outcome.Code, outcome.JobId, user?.Id, cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch
        {
            await log.ForgetAsync(message.Provider, message.ExternalMessageId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InboundOutcome> RouteAsync(InboundMessage message, AgentdUser user, CancellationToken ct)
    {
        var conversation = await conversations.FindExternalAsync(message.Provider, message.ExternalConversationId, ct).ConfigureAwait(false);
        if (message.Command is not null)
        {
            return await commands.ExecuteAsync(message, user, conversation, ct).ConfigureAwait(false);
        }

        if (conversation is null)
        {
            // Not a job's thread: maybe an idea's brainstorm thread.
            if (ideas is not null && ideaStore is not null && !string.IsNullOrWhiteSpace(message.Text)
                && await ideaStore.FindByThreadAsync(message.Provider, message.ExternalConversationId, ct).ConfigureAwait(false) is { } idea)
            {
                return new InboundOutcome(await ideas.HandleMessageAsync(idea, user.Name, message.Text, ct).ConfigureAwait(false) ? "idea" : "idea_closed");
            }

            return new InboundOutcome("ignored_no_job");
        }

        // A button press carries the option's label as its text.
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return new InboundOutcome("ignored_empty", conversation.JobId);
        }

        // "1"–"4" / allow / always / deny answer an open permission request first (the agent is waiting on it).
        if (permissions is not null
            && (await permissions.Handle(new Permissions.PermissionAnswer(conversation.JobId, message.Text, user.Name), ct).ConfigureAwait(false)) is { IsSuccess: true, Value: true })
        {
            return new InboundOutcome("permission", conversation.JobId);
        }

        if ((await closeOut.Handle(new AnswerCloseOut(conversation.JobId, message.Text), ct).ConfigureAwait(false)) is { IsSuccess: true, Value: true })
        {
            return new InboundOutcome("close_out", conversation.JobId);
        }

        var result = await submit.Handle(new SubmitDeveloperMessage(conversation.JobId, message.Text, user.Name, message.Provider), ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new InboundOutcome($"rejected:{result.Error.Code}", conversation.JobId);
        }

        if (result.Value == DeveloperMessageOutcome.NotAccepted)
        {
            var state = (await jobs.GetAsync(conversation.JobId, ct).ConfigureAwait(false))?.State;
            await outbox.EnqueueAsync(conversation.JobId,
                [new OutboxMessage(new OutboundMessage(MessageKind.Info, NotAcceptedText(state)), new EnqueueOptions(OnlyProviders: [message.Provider]))],
                ct).ConfigureAwait(false);
            return new InboundOutcome("not_accepted", conversation.JobId);
        }

        if (result.Value == DeveloperMessageOutcome.Queued
            && await jobs.GetAsync(conversation.JobId, ct).ConfigureAwait(false) is { } job)
        {
            // "What's the progress?" mid-task: answer now with the live status; the agent gets the message at its next step.
            var status = JobActivity.Describe(job, activity.Get(job.Id), clock.UtcNow);
            await outbox.EnqueueAsync(conversation.JobId,
                [new OutboxMessage(new OutboundMessage(MessageKind.Info, $"**Status:** {status}\n\nYour message reaches the agent at its next step."), new EnqueueOptions(OnlyProviders: [message.Provider]))],
                ct).ConfigureAwait(false);
        }

        return new InboundOutcome(result.Value switch
        {
            DeveloperMessageOutcome.Resumed => "resumed",
            DeveloperMessageOutcome.HandoffDeclined => "handoff_declined",
            DeveloperMessageOutcome.FixRound => "fix_round",
            _ => "queued",
        }, conversation.JobId);
    }

    /// <summary>Why the message wasn't delivered, and what to do instead.</summary>
    internal static string NotAcceptedText(JobState? state) => state switch
    {
        JobState.Queued or JobState.Preparing => "⏳ This job hasn't started yet, so your message wasn't delivered. Send it again once the agent is working (you'll see it here).",
        JobState.Publishing => "📤 agentd is pushing and opening the pull request right now. Send your message again in a moment; during review it starts a fix round.",
        JobState.Failed => "❌ This job failed, so your message wasn't delivered. Use `!retry` to run it again (it resumes where it stopped), then send your message.",
        JobState.Paused => "⏸ This job is paused, so your message wasn't delivered. `!resume` continues it, then send your message.",
        JobState.Cancelled => "🚫 This job was cancelled, so your message wasn't delivered. `!retry` resumes it where it stopped, or `!run <work item id>` starts a new run.",
        JobState.Done => "✅ This job is done, so your message wasn't delivered. For more changes, use `!run <work item id>` to start a new run.",
        _ => "This job can't take messages right now, so your message wasn't delivered.",
    };

    /// <summary>A stranger on a provider that allows everyone: acts under their display name; not stored as a user.</summary>
    private AgentdUser? Guest(InboundMessage message)
    {
        if (messaging?.CurrentValue.Providers.TryGetValue(message.Provider.Value, out var settings) != true || !settings!.AllowEveryone)
        {
            return null;
        }

        LogGuest(logger, message.Provider.Value, message.ExternalUserId);
        var name = string.IsNullOrWhiteSpace(message.UserDisplayName) ? $"{message.Provider.Value}:{message.ExternalUserId}" : message.UserDisplayName.Trim();
        return new AgentdUser(new UserId(0), name, [GuestRole], true);
    }

    private InboundOutcome Unknown(InboundMessage message)
    {
        LogUnknownUser(logger, message.Provider.Value, message.ExternalUserId);
        return new InboundOutcome("ignored_unknown_user");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted a {Provider} message from {ExternalUserId}, who isn't in Agentd:Users (AllowEveryone is on)")]
    private static partial void LogGuest(ILogger logger, string provider, string externalUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignored a {Provider} message from unknown or inactive user {ExternalUserId}")]
    private static partial void LogUnknownUser(ILogger logger, string provider, string externalUserId);
}
