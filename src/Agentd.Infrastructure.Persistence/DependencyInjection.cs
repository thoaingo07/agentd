using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence services (repositories calling PostgreSQL routines, from Phase 1).
    /// Expects an <see cref="Npgsql.NpgsqlDataSource"/> registered by the host
    /// (e.g. Aspire's <c>AddNpgsqlDataSource("agentd")</c>). The schema is owned by Agentd.Migrator.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services) => services;
}
