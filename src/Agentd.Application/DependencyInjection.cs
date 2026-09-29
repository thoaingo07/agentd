using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Application;

public static class DependencyInjection
{
    /// <summary>Registers the use-case handlers. Ports are registered by Infrastructure; options by the Host.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<NoMatchNotices>();
        services.AddScoped<ICommandHandler<ClaimWorkItem, JobId>, ClaimWorkItemHandler>();
        services.AddScoped<ICommandHandler<PollWorkItems, int>, PollWorkItemsHandler>();
        services.AddScoped<ICommandHandler<StartNextJob, AgentRunRequest?>, StartNextJobHandler>();
        services.AddScoped<ICommandHandler<HandleAgentExit, JobState>, HandleAgentExitHandler>();
        services.AddScoped<ICommandHandler<RecordAgentOutput, long>, RecordAgentOutputHandler>();
        services.AddScoped<ICommandHandler<FinishWork, PullRequestRef>, FinishWorkHandler>();
        services.AddScoped<ICommandHandler<PublishPullRequest, PullRequestRef>, PublishPullRequestHandler>();
        services.AddScoped<ICommandHandler<CancelJob, Unit>, CancelJobHandler>();
        services.AddScoped<ICommandHandler<RecoverJobsOnStartup, RecoveryPlan>, RecoverJobsOnStartupHandler>();
        services.AddScoped<IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>, GetJobStatusHandler>();
        return services;
    }
}
