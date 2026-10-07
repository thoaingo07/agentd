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

        return setup;
    }

    /// <summary>The PostgreSQL connection string (a secret). On Test, empty means "the saved one".</summary>
    public sealed record DatabaseRequest(string? ConnectionString);

    private static IResult? TooLong(string? secret) => secret is { Length: > MaxSecretLength }
        ? new Domain.Common.DomainError("validation", $"At most {MaxSecretLength} characters.").ToProblem()
        : null;

    private static IResult Saved(SaveResult r) => TypedResults.Ok(new SaveResultVm(r.RestartRequired));

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<StepCheckVm> Check(StepCheck c) => TypedResults.Ok(new StepCheckVm(c.Ok, c.Message, c.Fix));

    private static string By(ClaimsPrincipal user) => user.Identity?.Name ?? "setup";
}
