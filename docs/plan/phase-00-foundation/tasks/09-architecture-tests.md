# T0.9 — Architecture tests (dependency rule)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 0 | T0.2 | S | tests/Agentd.ArchitectureTests |

## Goal
Make the Clean Architecture dependency rule **fail the build** when it's broken, for both project
references and package references, so it never erodes silently.

## Files
- `tests/Agentd.ArchitectureTests/ProjectGraph.cs`: create. Parses the `.csproj` files.
- `tests/Agentd.ArchitectureTests/DependencyRuleTests.cs`: create.

## Implementation
1. **Read the declared dependencies, not the compiled ones.** The compiler drops unused assembly
   references, so reflection would miss a reference that was added but not used yet. Parse each
   `src/**/*.csproj` with `System.Xml.Linq` and collect `ProjectReference` names,
   `PackageReference` IDs and `FrameworkReference` names. Resolve the repo root by walking up from
   `AppContext.BaseDirectory` to the directory containing `Agentd.slnx`.
2. Rules (data-driven: one MSTest `[TestMethod]` fed by `[DynamicData]`, one row per rule, so each
   rule shows up as its own test case):

   | Project | Must not reference (projects) | Must not reference (packages / frameworks, by prefix) |
   |---|---|---|
   | Domain | any `Agentd.*` | anything except analyzers |
   | Application | `Agentd.Infrastructure.*`, `Agentd.Bff`, `Agentd.Mcp`, `Agentd.Host` | `Microsoft.EntityFrameworkCore`, `Npgsql`, `Microsoft.AspNetCore.App` (framework), `Discord`, `Telegram`, `Microsoft.Agents.AI` |
   | Infrastructure.* | `Agentd.Bff`, `Agentd.Mcp`, `Agentd.Host`, other `Agentd.Infrastructure.*` | — |
   | Bff, Mcp | `Agentd.Infrastructure.*`, `Agentd.Host` | `Microsoft.EntityFrameworkCore`, `Npgsql` |
   | any except Host | `Agentd.ServiceDefaults` | — |

   Allowed for Application: `Microsoft.Extensions.*.Abstractions`.
3. The failure message names the project, the forbidden dependency and the rule, e.g.
   `Agentd.Application → Microsoft.EntityFrameworkCore violates "Application is framework-free"`.
4. Phase 4 adds a rule: only `Agentd.Infrastructure.Orchestration` may reference `Microsoft.Agents.AI*`.
   Keep the rules table easy to extend.

## Tests
- The rules themselves, plus one **self-test**: a fixture `.csproj` string containing a forbidden
  reference must produce a violation. This proves the parser works.

## Done when
- [ ] All rules pass on the current solution.
- [ ] Temporarily adding `<PackageReference Include="Microsoft.EntityFrameworkCore" />` to
      Application makes `dotnet test` fail with a readable message.
