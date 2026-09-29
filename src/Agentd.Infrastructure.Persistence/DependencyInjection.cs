using Agentd.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Infrastructure.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence services. Expects an <see cref="Npgsql.NpgsqlDataSource"/> to be registered
    /// by the host (e.g. Aspire's <c>AddNpgsqlDataSource("agentd")</c>).
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}
