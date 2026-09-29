using Agentd.Bff;
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
builder.Services.AddSpaHosting(builder.Configuration);

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseSpaHosting();                     // Razor shell + Vite (manifest in production, dev-server proxy in Development)

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed for <c>WebApplicationFactory&lt;Program&gt;</c> in tests.</summary>
public partial class Program;
