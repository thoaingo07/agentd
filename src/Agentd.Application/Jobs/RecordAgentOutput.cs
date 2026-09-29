using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>Append one parsed agent output event (from the stream-json transcript) to the event log.</summary>
public sealed record RecordAgentOutput(JobId JobId, string Type, string PayloadJson);

public sealed class RecordAgentOutputHandler(IEventStore events) : ICommandHandler<RecordAgentOutput, long>
{
    public async Task<Result<long>> Handle(RecordAgentOutput command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await events.AppendAsync(command.JobId, command.Type, command.PayloadJson, cancellationToken).ConfigureAwait(false);
    }
}
