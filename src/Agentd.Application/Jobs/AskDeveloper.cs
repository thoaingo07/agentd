using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>The agent asks the developer (MCP <c>ask_developer</c>); the job waits for a reply.</summary>
public sealed record AskDeveloper(JobId JobId, string Question, IReadOnlyList<string> Options);

/// <summary>
/// Validates the question (1–2,000 characters, up to 5 short options) and moves the job to
/// WaitingForHuman. The question is posted to every conversation by the job's event (outbox).
/// </summary>
public sealed class AskDeveloperHandler(IJobRepository jobs) : ICommandHandler<AskDeveloper, Unit>
{
    public const int MaxQuestionLength = 2000;
    public const int MaxOptions = 5;
    public const int MaxOptionLength = 80;

    public async Task<Result<Unit>> Handle(AskDeveloper command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Question) || command.Question.Length > MaxQuestionLength)
        {
            return DomainError.Validation($"The question must be 1–{MaxQuestionLength} characters.");
        }

        if (command.Options.Count > MaxOptions || command.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > MaxOptionLength))
        {
            return DomainError.Validation($"Give at most {MaxOptions} options of 1–{MaxOptionLength} characters each.");
        }

        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var asked = job.AskDeveloper(command.Question, command.Options.Select(o => o.Trim()).ToList());
        if (!asked.IsSuccess)
        {
            return asked.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}

/// <summary>The agent reports progress (MCP <c>report_progress</c>): it updates the live status message.</summary>
public sealed record ReportProgress(JobId JobId, string Message);

public sealed class ReportProgressHandler(IOutbox outbox, IEventStore events) : ICommandHandler<ReportProgress, Unit>
{
    public const int MaxLength = 500;

    public async Task<Result<Unit>> Handle(ReportProgress command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Message))
        {
            return DomainError.Validation("The progress message must not be empty.");
        }

        var text = command.Message.Length > MaxLength ? command.Message[..MaxLength] + "…" : command.Message.Trim();
        await events.AppendAsync(command.JobId, "progress.reported", JsonSerializer.Serialize(new { message = text }), cancellationToken).ConfigureAwait(false);
        // A visible message: the live status line is the heartbeat (re-posted every minute).
        await outbox.EnqueueAsync(command.JobId, [new OutboxMessage(MessageCatalog.Progress($"⏳ {text}"))], cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
