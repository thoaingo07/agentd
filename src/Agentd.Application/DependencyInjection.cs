using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Agentd.Application.Users;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
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
        services.AddScoped<ICommandHandler<RetryDuePublishes, int>, RetryDuePublishesHandler>();
        services.AddSingleton<JobDispatcher>();
        services.AddScoped<IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>, GetJobStatusHandler>();
        services.AddScoped<ICommandHandler<AddRepository, Repository>, AddRepositoryHandler>();
        services.AddScoped<ICommandHandler<RemoveRepository, Unit>, RemoveRepositoryHandler>();
        services.AddScoped<ICommandHandler<SeedRepositories, SeedResult>, SeedRepositoriesHandler>();
        services.AddScoped<ICommandHandler<SeedUsers, int>, SeedUsersHandler>();
        services.AddSingleton<ConversationTargetsResolver>();
        services.AddSingleton<IMessagingProviderRegistry, MessagingProviderRegistry>();
        services.AddScoped<MessagingService>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddScoped<ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome>, SubmitDeveloperMessageHandler>();
        services.AddScoped<ICommandHandler<RetryJob, int>, RetryJobHandler>();
        services.AddScoped<ChatCommands>();
        services.AddScoped<ICommandHandler<RepairConversations, int>, RepairConversationsHandler>();
        services.AddScoped<InboundMessageHandler>();
        services.AddScoped<IInboundMessageSink>(sp => sp.GetRequiredService<InboundMessageHandler>());
        return services;
    }
}
