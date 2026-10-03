using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>
/// Hand the developer's unread messages to the running agent now (piggybacked on an agentd tool
/// result), instead of waiting for its next turn. Empty when there are none.
/// </summary>
public sealed record TakeDeveloperMessages(JobId JobId);

public sealed class TakeDeveloperMessagesHandler(IJobRepository jobs) : ICommandHandler<TakeDeveloperMessages, IReadOnlyList<string>>
{
    private const int MaxAttempts = 5;

    public async Task<Result<IReadOnlyList<string>>> Handle(TakeDeveloperMessages command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        for (var attempt = 1; ; attempt++)
        {
            var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
            if (job is null || job.PendingMessages.Count == 0)
            {
                return Result.Success<IReadOnlyList<string>>([]);
            }

            var messages = job.TakePendingMessages();
            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            if (saved.IsSuccess)
            {
                return Result.Success(messages);
            }

            // A reply landed meanwhile (version conflict): reload and take that one too.
            if (saved.Error.Code != "conflict" || attempt == MaxAttempts)
            {
                return saved.Error;
            }
        }
    }

    /// <summary>How unread messages are shown to the agent inside a tool result.</summary>
    public static string Format(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return "\n\n---\nThe developer wrote while you were working (address this before continuing):\n" +
               string.Join("\n", messages.Select(m => "- " + m));
    }
}
