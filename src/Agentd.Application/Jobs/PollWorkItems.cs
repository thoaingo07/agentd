using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>Query tagged, unclaimed work items and claim each one.</summary>
public sealed record PollWorkItems;

public sealed class PollWorkItemsHandler(
    IWorkItemSource workItems,
    ICommandHandler<ClaimWorkItem, JobId> claim,
    IOptions<JobOptions> options) : ICommandHandler<PollWorkItems, int>
{
    public async Task<Result<int>> Handle(PollWorkItems command, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var candidates = await workItems.QueryTaggedAsync(o.Tag, o.ClaimTag, o.States.ToList(), cancellationToken).ConfigureAwait(false);
        var claimed = 0;
        foreach (var candidate in candidates)
        {
            if (WorkItemId.Create(candidate.Id) is not { IsSuccess: true } id)
            {
                continue;
            }

            var result = await claim.Handle(new ClaimWorkItem(id.Value), cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                claimed++;
            }
        }

        return claimed;
    }
}
