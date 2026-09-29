# T0.5 — Host startup & strongly typed options

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.3 | S | Agentd.Host |

## Goal
The Host is the composition root. It binds and validates configuration at startup, wires in the
service defaults, and listens on loopback by default when it runs outside Aspire.

## Files
- `src/Agentd.Host/Program.cs`: modify. Composition root.
- `src/Agentd.Host/Options/AgentdOptions.cs`: create. The root options + `WebOptions` for now.
- `src/Agentd.Host/appsettings.json`: create. Defaults (no secrets).
- `src/Agentd.Host/appsettings.Development.json`: create. Verbose logging.
- `src/Agentd.Host/Properties/launchSettings.json`: modify. The `http` profile at `127.0.0.1:7780`.

## Implementation
1. Options root, **section `Agentd`** (matches [Architecture §3.1](../../../architect/README.md#31-config)):
   ```csharp
   public sealed class AgentdOptions
   {
       public const string Section = "Agentd";
       [Required] public WebOptions Web { get; init; } = new();
   }
   public sealed class WebOptions
   {
       [Required, Url] public string Urls { get; init; } = "http://127.0.0.1:7780";
   }
   ```
   Later phases add their own subsections (`AzureDevOps`, `Messaging`, …), each with its own
   options class in the project that consumes it.
2. `Program.cs`:
   ```csharp
   var builder = WebApplication.CreateBuilder(args);
   builder.AddServiceDefaults();
   builder.Services.AddOptions<AgentdOptions>()
          .BindConfiguration(AgentdOptions.Section)
          .ValidateDataAnnotations()
          .ValidateOnStart();
   // persistence: T0.6 · static SPA: T0.8
   var app = builder.Build();
   app.MapDefaultEndpoints();
   app.Run();
   ```
3. **Listening address:**
   - under Aspire, the AppHost sets the endpoints, so don't override them;
   - otherwise, if neither `ASPNETCORE_URLS` nor `--urls` is set, use `Agentd:Web:Urls` (default
     loopback). Implement this as `builder.WebHost.UseUrls(opts.Web.Urls)` behind a check for those
     two sources.
4. Secrets: `builder.Configuration.AddUserSecrets<Program>(optional: true)` in Development only.
   Env vars use the `Agentd__…` naming.

## Tests
- `Bff.Tests`: the Host starts under `WebApplicationFactory` with the default config.
- A unit test: invalid options (`Web:Urls = "not a url"`) → startup throws `OptionsValidationException`.

## Done when
- [ ] `dotnet run --project src/Agentd.Host` (no Aspire) listens on `127.0.0.1:7780` only.
- [ ] Bad configuration fails fast at startup with a clear message.
- [ ] No secrets are in any committed `appsettings*.json`.
