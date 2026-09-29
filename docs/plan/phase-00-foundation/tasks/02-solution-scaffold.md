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
- `tests/Agentd.{Domain,Application,Infrastructure,Bff,Architecture}Tests/…csproj`: create, using **MSTest on Microsoft.Testing.Platform** (`<Project Sdk="MSTest.Sdk">`).

## Implementation
1. Create the projects with `dotnet new classlib|web|mstest`, then delete the template sample files.
   Test projects use `<Project Sdk="MSTest.Sdk">`, with the version from `global.json` (T0.1).
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
6. **Test stack: MSTest + Microsoft.Testing.Platform (MTP).**
   - `MSTest.Sdk` brings the MSTest framework, analyzers, the MTP runner, and the default extensions
     profile (TRX reports + code coverage). There's no `Microsoft.NET.Test.Sdk`, and no
     VSTest adapter.
   - Test projects are **executables** (MTP). `dotnet test` in MTP mode (T0.1) and `dotnet run`
     both run them.
   - **Assertions:** MSTest's built-in `Assert`, `CollectionAssert` and `StringAssert`. No third-party
     assertion library.
   - Extra packages go in `Directory.Packages.props`: `Microsoft.AspNetCore.Mvc.Testing`
     (Bff.Tests) and `Testcontainers.PostgreSql` (Infrastructure.Tests).
   - Check that `MSTest.Sdk` works with central package management. The SDK supplies its own package
     versions; follow the MSTest docs if it needs an override.
   - Parallelization: unit test projects declare
     `[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`. Integration tests that share a
     container use `ClassLevel`, plus `[DoNotParallelize]` where needed.
   - Categories: `[TestCategory("Integration")]` (needs Docker) and `[TestCategory("Aspire")]`
     (starts the AppHost).

## Tests
- A trivial test in each test project, so the pipeline is proven end to end:
  ```csharp
  [TestClass]
  public sealed class SmokeTests
  {
      [TestMethod]
      public void Pipeline_runs() => Assert.IsTrue(true);
  }
  ```

## Done when
- [ ] `dotnet build Agentd.slnx` succeeds with zero warnings.
- [ ] `dotnet test` runs all test projects, and they pass.
- [ ] The reference graph matches the table exactly (T0.9 enforces this).
