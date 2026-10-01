using Agentd.Application.Ports;
using Agentd.Infrastructure.Persistence.Repositories;
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
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton<IRepositoryRegistry, RepositoryStore>();
        return services;
    }
}
