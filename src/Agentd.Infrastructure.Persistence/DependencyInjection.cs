using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Application.Users;
using Agentd.Infrastructure.Persistence.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;
using Agentd.Infrastructure.Persistence.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers repositories that call PostgreSQL routines. Expects an <see cref="Npgsql.NpgsqlDataSource"/>
    /// (e.g. Aspire's <c>AddNpgsqlDataSource("agentd")</c>) and an <see cref="Domain.Common.IClock"/>.
    /// The schema is owned by Agentd.Migrator.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddSingleton<JobRepository>();
        services.AddSingleton<IJobRepository>(sp => sp.GetRequiredService<JobRepository>());
        services.AddSingleton<IJobSearch>(sp => sp.GetRequiredService<JobRepository>());
        services.AddSingleton<EventStore>();
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<EventStore>());
        services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<EventStore>());
        services.AddSingleton<IRepositoryRegistry, RepositoryStore>();
        services.AddSingleton<IWorkItemHistory, WorkItemHistoryStore>();
        services.AddSingleton<Application.Permissions.IPermissionStore, PermissionStore>();
        services.AddSingleton<Application.Ideas.IIdeaStore, IdeaStore>();
        services.AddSingleton<Application.Reviews.IReviewStore, ReviewStore>();
        services.AddSingleton<Application.Chats.IChatStore, ChatStore>();
        services.AddSingleton<IAdoUserConnections, AdoUserConnectionStore>();
        services.AddSingleton<Application.Reviews.IReviewSessionStore, ReviewSessionStore>();
        services.AddSingleton<JobSessionStore>();
        services.AddSingleton<Application.Jobs.IJobSessions>(sp => sp.GetRequiredService<JobSessionStore>());
        services.AddSingleton<Application.Jobs.IJobPlans>(sp => sp.GetRequiredService<JobSessionStore>());
        services.AddSingleton<IConversationStore, ConversationStore>();
        services.AddSingleton<IUserDirectory, UserDirectory>();
        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IOutboxDelivery, OutboxDelivery>();
        services.AddSingleton<IInboundLog, InboundLog>();
        services.AddSingleton<Application.Events.EventHub>();
        services.AddSingleton<Application.Events.ILiveEvents>(sp => sp.GetRequiredService<Application.Events.EventHub>());
        return services;
    }

    /// <summary>The daemon's background work for the event log: live fan-out and partition upkeep.</summary>
    public static IServiceCollection AddEventStreaming(this IServiceCollection services)
    {
        services.AddHostedService<Events.EventNotificationListener>();
        services.AddHostedService<Events.EventPartitionMaintenance>();
        return services;
    }
}
