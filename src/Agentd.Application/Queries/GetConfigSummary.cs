using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Queries;

/// <summary>The settings the UI shows read-only. Built field by field, so no secret can slip in.</summary>
public sealed record ConfigSummary(
    string Tag,
    string ClaimTag,
    TimeSpan PollInterval,
    int MaxConcurrent,
    bool RequirePlanApproval,
    bool ReviewLoop,
    bool Handoff,
    IReadOnlyList<RepositorySummary> Repositories,
    IReadOnlyList<string> MessagingProviders);

public sealed record RepositorySummary(string Name, string Organization, string Project, string BaseBranch);

public sealed record GetConfigSummary;

public sealed class GetConfigSummaryHandler(
    IOptions<JobOptions> jobs,
    IOptions<SchedulerOptions> scheduler,
    IOptions<MessagingOptions> messaging,
    IRepositoryRegistry repositories) : IQueryHandler<GetConfigSummary, ConfigSummary>
{
    public async Task<ConfigSummary> Handle(GetConfigSummary query, CancellationToken cancellationToken)
    {
        var j = jobs.Value;
        var repos = await repositories.ListAsync(cancellationToken).ConfigureAwait(false);
        return new ConfigSummary(
            j.Tag,
            j.ClaimTag,
            scheduler.Value.PollInterval,
            scheduler.Value.MaxConcurrent,
            j.RequirePlanApproval,
            j.ReviewLoop,
            j.Handoff,
            repos.Select(r => new RepositorySummary(r.Name.Value, r.AzureDevOps.Organization, r.AzureDevOps.Project, r.BaseBranch)).ToList(),
            messaging.Value.EnabledKeys().Select(k => k.Value).ToList());
    }
}
