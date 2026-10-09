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

    /// <summary>
    /// Maps the steps on <paramref name="setup"/>: once on <c>/api/setup</c> for the wizard (with Finish), and once on
    /// <c>/api/settings</c> for Admins (Settings and Health). <paramref name="prefix"/> keeps the operation names unique.
    /// </summary>
    public static RouteGroupBuilder MapSetupSteps(this RouteGroupBuilder setup, string prefix = "Setup", bool finish = true)
    {
        ArgumentNullException.ThrowIfNull(setup);

        setup.MapGet("/database", ([FromServices] SetupService service) => TypedResults.Ok(new DatabaseStepVm(SecretStatusVm.From(service.GetDatabase().ConnectionString))))
            .WithName($"Get{prefix}Database");
        setup.MapPut("/database", async (DatabaseRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.ConnectionString) ?? (await service.SaveDatabaseAsync(body?.ConnectionString ?? string.Empty, By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Save{prefix}Database").Accepts<DatabaseRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/database/test", async (DatabaseRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.ConnectionString) ?? Check(await service.TestDatabaseAsync(body?.ConnectionString, ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}Database").Accepts<DatabaseRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/database/migrate", async (ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            Check(await service.MigrateDatabaseAsync(By(user), ct).ConfigureAwait(false)))
            .WithName($"Migrate{prefix}Database").Produces<StepCheckVm>();

        setup.MapGet("/azure-devops", ([FromServices] SetupService service) =>
            {
                var step = service.GetAzureDevOps();
                return TypedResults.Ok(new AzureDevOpsStepVm(step.Organization, step.Project, step.Auth, SecretStatusVm.From(step.Pat),
                    step.TenantId, step.ClientId, SecretStatusVm.From(step.ClientSecret ?? SecretStatus.Missing)));
            })
            .WithName($"Get{prefix}AzureDevOps");
        setup.MapPut("/azure-devops", async (AzureDevOpsRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.Pat) ?? TooLong(body?.ClientSecret) ?? (await service.SaveAzureDevOpsAsync(Input(body), By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Save{prefix}AzureDevOps").Accepts<AzureDevOpsRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/azure-devops/test", async (AzureDevOpsRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.Pat) ?? TooLong(body?.ClientSecret) ?? Check(await service.TestAzureDevOpsAsync(body is null ? null : Input(body), ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}AzureDevOps").Accepts<AzureDevOpsRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        setup.MapGet("/git-key", ([FromServices] SetupService service) => TypedResults.Ok(GitKeyStepVm.From(service.GetGitKey())))
            .WithName($"Get{prefix}GitKey");
        setup.MapPost("/git-key", async (ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            (await service.GenerateGitKeyAsync(By(user), ct).ConfigureAwait(false)).ToHttpResult(k => TypedResults.Ok(GitKeyStepVm.From(k))))
            .WithName($"Generate{prefix}GitKey").Produces<GitKeyStepVm>().ProducesProblem(StatusCodes.Status409Conflict);
        setup.MapPost("/git-key/test", async (GitTestRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            Check(await service.TestGitAccessAsync(body?.Url, ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}GitAccess").Accepts<GitTestRequest>("application/json").Produces<StepCheckVm>();

        setup.MapGet("/claude", async ([FromServices] SetupService service, CancellationToken ct) =>
                TypedResults.Ok(ClaudeStepVm.From(await service.GetClaudeAsync(ct).ConfigureAwait(false))))
            .WithName($"Get{prefix}Claude");
        setup.MapPut("/claude", async (ClaudeTokenRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.Token) ?? (await service.SaveClaudeTokenAsync(body?.Token ?? string.Empty, By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Save{prefix}ClaudeToken").Accepts<ClaudeTokenRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapDelete("/claude/token", async (ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            Saved(await service.RemoveClaudeTokenAsync(By(user), ct).ConfigureAwait(false)))
            .WithName($"Remove{prefix}ClaudeToken").Produces<SaveResultVm>();
        setup.MapPost("/claude/test", async (ClaudeTokenRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.Token) ?? Check(await service.TestClaudeAsync(body?.Token, ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}Claude").Accepts<ClaudeTokenRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        setup.MapGet("/chat", ([FromServices] SetupService service) => TypedResults.Ok(ChatStepVm.From(service.GetChat())))
            .WithName($"Get{prefix}Chat");
        setup.MapPut("/chat", async (ChatRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.BotToken) ?? (await service.SaveChatAsync(Chat(body), By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Save{prefix}Chat").Accepts<ChatRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPost("/chat/test", async (ChatRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.BotToken) ?? Check(await service.TestChatAsync(Chat(body), ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}Chat").Accepts<ChatRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        setup.MapGet("/repositories", ([FromServices] SetupService service) =>
                TypedResults.Ok(service.GetRepositories().Select(RepositoryEntryVm.From).ToList()))
            .WithName($"Get{prefix}Repositories");
        setup.MapPost("/repositories", async (RepositoryRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            (await service.AddRepositoryAsync(new RepositoryInput(body?.Url ?? string.Empty, body?.Name, body?.BaseBranch, body?.MatchTag, body?.MatchAreaPaths), By(user), ct).ConfigureAwait(false))
                .ToHttpResult(r => TypedResults.Ok(RepositoryEntryVm.From(r))))
            .WithName($"Add{prefix}Repository").Accepts<RepositoryRequest>("application/json").Produces<RepositoryEntryVm>()
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);
        setup.MapPost("/repositories/test", async (GitTestRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            Check(await service.TestRepositoryAsync(body?.Url, ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}Repository").Accepts<GitTestRequest>("application/json").Produces<StepCheckVm>();

        setup.MapGet("/models", ([FromServices] SetupService service) => TypedResults.Ok(ModelsStepVm.From(service.GetModels())))
            .WithName($"Get{prefix}Models");
        setup.MapPut("/models/profiles", async (ProfileRequest? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.ApiKey) ?? (await service.SaveProfileAsync(Profile(body), By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Save{prefix}ModelProfile").Accepts<ProfileRequest>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapDelete("/models/profiles/{name}", async (string name, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            (await service.RemoveProfileAsync(name, By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Remove{prefix}ModelProfile").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status409Conflict);
        setup.MapPost("/models/profiles/test", async (ProfileRequest? body, [FromServices] SetupService service, CancellationToken ct) =>
            TooLong(body?.ApiKey) ?? Check(await service.TestProfileAsync(Profile(body), ct).ConfigureAwait(false)))
            .WithName($"Test{prefix}ModelProfile").Accepts<ProfileRequest>("application/json").Produces<StepCheckVm>().ProducesProblem(StatusCodes.Status400BadRequest);
        setup.MapPut("/models/steps", async (IReadOnlyList<StepModelVm>? body, ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            (await service.SaveStepsAsync([.. (body ?? []).Select(s => new StepView(s.Step, s.Model, s.Effort, s.Profile))], By(user), ct).ConfigureAwait(false)).ToHttpResult(Saved))
            .WithName($"Save{prefix}ModelSteps").Accepts<IReadOnlyList<StepModelVm>>("application/json").Produces<SaveResultVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        setup.MapGet("/review", async ([FromServices] SetupService service, CancellationToken ct) =>
                TypedResults.Ok((await service.ReviewAsync(ct).ConfigureAwait(false)).Select(ReviewItemVm.From).ToList()))
            .WithName($"Get{prefix}Review");
        if (!finish)
        {
            return setup;
        }

        setup.MapPost("/finish", async (ClaimsPrincipal user, [FromServices] SetupService service, CancellationToken ct) =>
            (await service.FinishAsync(By(user), ct).ConfigureAwait(false)).ToHttpResult(f => TypedResults.Ok(new FinishVm(f.CompletedAt))))
            .WithName($"Finish{prefix}").Produces<FinishVm>().ProducesProblem(StatusCodes.Status400BadRequest);

        return setup;
    }

    /// <summary>The PostgreSQL connection string (a secret). On Test, empty means "the saved one".</summary>
    public sealed record DatabaseRequest(string? ConnectionString);

    /// <summary><c>Auth</c> is <c>Pat</c> or <c>AzCli</c>. An empty <c>Pat</c> keeps the saved one.</summary>
    /// <param name="Organization">A name or URL.</param>
    /// <param name="Project">The project name.</param>
    /// <param name="Auth"><c>Pat</c>, <c>AzCli</c> or <c>ServicePrincipal</c>.</param>
    /// <param name="Pat">A new token; empty keeps the stored one.</param>
    /// <param name="TenantId">The service principal's directory (tenant) id.</param>
    /// <param name="ClientId">The service principal's application (client) id.</param>
    /// <param name="ClientSecret">A new client secret; empty keeps the stored one.</param>
    public sealed record AzureDevOpsRequest(string? Organization, string? Project, string? Auth, string? Pat, string? TenantId = null, string? ClientId = null, string? ClientSecret = null);

    /// <summary>A token from <c>claude setup-token</c>. On Test, empty means "the saved one, else the server's login".</summary>
    public sealed record ClaudeTokenRequest(string? Token);

    /// <summary>Discord settings. An empty <c>BotToken</c> keeps the saved one; <c>UserName</c> + <c>UserDiscordId</c> add you as a user.</summary>
    public sealed record ChatRequest(bool Enabled, string? GuildId, string? ChannelId, string? BotToken, string? UserName, string? UserDiscordId);

    /// <summary>A repository to add. Only <c>Url</c> is required; the rest defaults like <c>agentd repo add</c>.</summary>
    public sealed record RepositoryRequest(string? Url, string? Name, string? BaseBranch, string? MatchTag, IReadOnlyList<string>? MatchAreaPaths);

    /// <summary>A provider (Anthropic-compatible endpoint). An empty <c>ApiKey</c> keeps the saved one.</summary>
    public sealed record ProfileRequest(string? Name, string? BaseUrl, string? Model, string? SmallModel, string? ApiKey);

    /// <summary>A repository's clone URL (SSH for agentd's key).</summary>
    public sealed record GitTestRequest(string? Url);

    private static AzureDevOpsInput Input(AzureDevOpsRequest? body) =>
        new(body?.Organization ?? string.Empty, body?.Project ?? string.Empty, body?.Auth ?? SetupService.AzCliAuth, body?.Pat, body?.TenantId, body?.ClientId, body?.ClientSecret);

    private static ProfileInput Profile(ProfileRequest? body) =>
        new(body?.Name ?? string.Empty, body?.BaseUrl, body?.Model, body?.SmallModel, body?.ApiKey);

    private static ChatInput Chat(ChatRequest? body) =>
        new(body?.Enabled ?? false, body?.GuildId, body?.ChannelId, body?.BotToken, body?.UserName, body?.UserDiscordId);

    private static IResult? TooLong(string? secret) => secret is { Length: > MaxSecretLength }
        ? new Domain.Common.DomainError("validation", $"At most {MaxSecretLength} characters.").ToProblem()
        : null;

    private static IResult Saved(SaveResult r) => TypedResults.Ok(new SaveResultVm(r.RestartRequired));

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<StepCheckVm> Check(StepCheck c) => TypedResults.Ok(new StepCheckVm(c.Ok, c.Message, c.Fix));

    private static string By(ClaimsPrincipal user) => user.Identity?.Name ?? "setup";
}
