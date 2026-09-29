using Agentd.Host;
using Agentd.Host.Options;
using Agentd.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOptions<AgentdOptions>()
    .BindConfiguration(AgentdOptions.Section)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AgentdOptions>, AgentdOptionsValidator>();

HostUrls.ApplyDefault(builder);

// PostgreSQL: pooled NpgsqlDataSource with health check and tracing (connection string "agentd").
builder.AddNpgsqlDataSource("agentd");
builder.Services.AddPersistence();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapDefaultEndpoints();
ReservedPaths.MapNotFound(app);          // /api, /bff, /hubs, /mcp never fall through to the SPA
app.MapFallbackToFile("index.html");

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed for <c>WebApplicationFactory&lt;Program&gt;</c> in tests.</summary>
public partial class Program;
