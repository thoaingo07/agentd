using System.Security.Claims;
using Agentd.Application.Setup;
using Agentd.Bff.Http;
using Agentd.Bff.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary>
/// <c>/api/setup/*</c>: the wizard's steps through <see cref="SetupService"/>, for the setup session only (T1b.11).
/// Secrets go in, never out: responses carry <see cref="SecretStatusVm"/>.
/// </summary>
public static class SetupEndpoints
{
    public const int MaxSecretLength = 4096;

    public static RouteGroupBuilder MapSetupSteps(this RouteGroupBuilder setup)
    {
        ArgumentNullException.ThrowIfNull(setup);

        setup.MapGet("/database", ([FromServices] SetupService service) => TypedResults.Ok(new DatabaseStepVm(SecretStatusVm.From(service.GetDatabase().ConnectionString))))
            .WithName("GetSetupDatabase");
        setup.MapPut("/database", async (DatabaseRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.ConnectionString) ?? (await service.SaveDatabaseAsync(body?.ConnectionString ?? string.Empty, By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName("SaveSetupDatabase").Accepts<DatabaseRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/database/test", async (DatabaseRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.ConnectionString) ?? Check(await service.TestDatabaseAsync(body?.ConnectionString, ct).ConfigureAwait(false)))
            .WithName("TestSetupDatabase").Accepts<DatabaseRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/database/migrate", async (ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            Check(await service.MigrateDatabaseAsync(By(user), ct).ConfigureAwait(false)))
            .WithName("MigrateSetupDatabase").Produces<StepCheckVm>();

        setup.MapGet("/azure-devops", ([FromServices] SetupService service) =>
            {
                var step = service.GetAzureDevOps();
                return TypedResults.Ok(new AzureDevOpsStepVm(step.Organization, step.Project, step.Auth, SecretStatusVm.From(step.Pat)));
            })
            .WithName("GetSetupAzureDevOps");
        setup.MapPut("/azure-devops", async (AzureDevOpsRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.Pat) ?? (await service.SaveAzureDevOpsAsync(Input(body), By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName("SaveSetupAzureDevOps").Accepts<AzureDevOpsRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/azure-devops/test", async (AzureDevOpsRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.Pat) ?? Check(await service.TestAzureDevOpsAsync(body is null ? null : Input(body), ct).ConfigureAwait(false)))
            .WithName("TestSetupAzureDevOps").Accepts<AzureDevOpsRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        return setup;
    }

    /// <summary>The PostgreSQL connection string (a secret). On Test, empty means "the saved one".</summary>
    public sealed record DatabaseRequest(string? ConnectionString);

    /// <summary><c>Auth</c> is <c>Pat</c> or <c>AzCli</c>. An empty <c>Pat</c> keeps the saved one.</summary>
    public sealed record AzureDevOpsRequest(string? Organization, string? Project, string? Auth, string? Pat);

    private static AzureDevOpsInput Input(AzureDevOpsRequest? body) =>
        new(body?.Organization ?? string.Empty, body?.Project ?? string.Empty, body?.Auth ?? SetupService.AzCliAuth, body?.Pat);

    private static IResult? TooLong(string? secret) => secret is { Length: > MaxSecretLength }
        ? new Domain.Common.DomainError("validation", $"At most {MaxSecretLength} characters.").ToProblem()
        : null;

    private static IResult Saved(SaveResult r) => TypedResults.Ok(new SaveResultVm(r.RestartRequired));

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<StepCheckVm> Check(StepCheck c) => TypedResults.Ok(new StepCheckVm(c.Ok, c.Message, c.Fix));

    private static string By(ClaimsPrincipal user) => user.Identity?.Name ?? "setup";
}
