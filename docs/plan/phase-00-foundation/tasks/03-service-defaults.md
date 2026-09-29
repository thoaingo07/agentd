# T0.3 — Agentd.ServiceDefaults (OTel, health, resilience)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.2 | S | Agentd.ServiceDefaults |

## Goal
One place for cross-cutting defaults (OpenTelemetry, health checks, HTTP resilience, service
discovery) that the Host opts into with `builder.AddServiceDefaults()`. It must work with and
without Aspire.

## Files
- `src/Agentd.ServiceDefaults/Extensions.cs`: create. `AddServiceDefaults()` and `MapDefaultEndpoints()`.
- `src/Agentd.ServiceDefaults/Agentd.ServiceDefaults.csproj`: modify. Set `<IsAspireSharedProject>true</IsAspireSharedProject>` and add the packages.
- `Directory.Packages.props`: modify. The OpenTelemetry packages (`OpenTelemetry.Extensions.Hosting`,
  `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`,
  `…Http`, `…Runtime`), `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.ServiceDiscovery`.

## Implementation
1. Start from the official `aspire-servicedefaults` template (`dotnet new aspire-servicedefaults`) and
   adapt it. Keep its structure so future Aspire upgrades are easy to diff.
2. `AddServiceDefaults()`:
   - `ConfigureOpenTelemetry()`: logging with formatted messages and scopes; metrics (ASP.NET Core,
     HttpClient, runtime); tracing (ASP.NET Core, HttpClient). Add the meter and ActivitySource
     named `Agentd`, which later phases use.
   - Only add the OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Aspire sets it in dev,
     and production may leave it unset.
   - `AddDefaultHealthChecks()`: a `self` check tagged `live`.
   - `ConfigureHttpClientDefaults(http => { http.AddStandardResilienceHandler(); http.AddServiceDiscovery(); })`.
3. `MapDefaultEndpoints()`, **customized** relative to the template, which maps only in Development:
   - `/healthz` → all checks (readiness). The response has **no details**: status text only.
   - `/alive` → only the checks tagged `live`.
   - Map both in **all environments**, because systemd and the reverse proxy need them. With no
     details in the response, they leak nothing.
4. Don't add anything that is specific to agentd's domain here.

## Tests
- `Bff.Tests` (added in T0.8): `GET /healthz` → 200 `Healthy`, and `GET /alive` → 200.

## Done when
- [ ] Under Aspire, Host traces and logs appear in the Aspire dashboard.
- [ ] Without `OTEL_EXPORTER_OTLP_ENDPOINT`, the Host starts with no exporter errors.
- [ ] `/healthz` and `/alive` respond in Development and in Production.
