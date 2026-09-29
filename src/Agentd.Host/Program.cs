using Agentd.Host;
using Agentd.Host.Options;
using Agentd.Infrastructure.Persistence;
using Agentd.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Serve Razor class library static assets (Agentd.Web's Vite build under /_content/Agentd.Web/) in every environment.
builder.WebHost.UseStaticWebAssets();

builder.AddServiceDefaults();

builder.Services.AddOptions<AgentdOptions>()
    .BindConfiguration(AgentdOptions.Section)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AgentdOptions>, AgentdOptionsValidator>();

HostUrls.ApplyDefault(builder);

// PostgreSQL: pooled NpgsqlDataSource with health check and tracing (connection string "agentd").
builder.AddNpgsqlDataSource("agentd");
builder.Services.AddSingleton<Agentd.Domain.Common.IClock, SystemClock>();
builder.Services.AddPersistence();
builder.Services.AddWebHosting(builder.Configuration);

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseWebHosting();                      // Razor shells for ClientApps/* (Vite manifest in production, dev-server proxy in Development)

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed for <c>WebApplicationFactory&lt;Program&gt;</c> in tests.</summary>
public partial class Program;
