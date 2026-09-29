# T0.2 — Solution & project scaffold (Clean Architecture)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.1 | S | all `src/*`, `tests/*` |

## Goal
Create every project with the right SDK and **only inward-pointing references**, so the dependency
rule holds from the first commit.

## Files
- `Agentd.slnx`: create.
- `src/Agentd.Domain/Agentd.Domain.csproj`: create (`Microsoft.NET.Sdk`).
- `src/Agentd.Application/Agentd.Application.csproj`: create (`Microsoft.NET.Sdk`).
- `src/Agentd.Infrastructure.Persistence/…csproj`: create (`Microsoft.NET.Sdk`).
- `src/Agentd.Bff/Agentd.Bff.csproj`: create (`Microsoft.NET.Sdk` + `<FrameworkReference Include="Microsoft.AspNetCore.App" />`).
- `src/Agentd.Mcp/Agentd.Mcp.csproj`: create (same as Bff).
- `src/Agentd.Host/Agentd.Host.csproj`: create (`Microsoft.NET.Sdk.Web`).
- `src/Agentd.ServiceDefaults/…csproj`: create (T0.3 fills it).
- `src/Agentd.AppHost/…csproj`: create (T0.4 fills it).
- `tests/Agentd.{Domain,Application,Infrastructure,Bff,Architecture}Tests/…csproj`: create (xUnit).

## Implementation
1. Create the projects with `dotnet new classlib|web|xunit`, then delete the template sample files.
2. Add them to `Agentd.slnx` with the solution folders `src` and `tests`.
3. Project references. **This table is the dependency rule:**

   | Project | References |
   |---|---|
   | Domain | — |
   | Application | Domain |
   | Infrastructure.Persistence | Application, Domain |
   | Bff | Application, Domain |
   | Mcp | Application, Domain |
   | Host | Bff, Mcp, Infrastructure.Persistence, ServiceDefaults |
   | AppHost | Host (as an Aspire project resource; see T0.4) |
   | Domain.Tests | Domain |
   | Application.Tests | Application |
   | Infrastructure.Tests | Infrastructure.Persistence |
   | Bff.Tests | Host (for `WebApplicationFactory<Program>`) |
   | ArchitectureTests | none (it reads the project files; see T0.9) |

4. Add a placeholder type per library, e.g. `Agentd.Domain.AssemblyMarker`, so each assembly is non-empty.
5. Host `Program.cs` is a minimal `WebApplication` for now. Add `public partial class Program;` so
   `WebApplicationFactory` can see it.
6. Test packages go in `Directory.Packages.props`: `xunit.v3` (or `xunit` 2.x; pick one and use it
   everywhere), `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `FluentAssertions` or
   `Shouldly` (one of them), and `Microsoft.AspNetCore.Mvc.Testing` (Bff.Tests).

## Tests
- A trivial `[Fact]` in each test project, so the pipeline is proven end to end.

## Done when
- [ ] `dotnet build Agentd.slnx` succeeds with zero warnings.
- [ ] `dotnet test` runs all test projects, and they pass.
- [ ] The reference graph matches the table exactly (T0.9 enforces this).
