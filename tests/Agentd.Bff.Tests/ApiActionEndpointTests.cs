using System.Net;
using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Permissions;
using Agentd.Application.Ports;
using Agentd.Application.Queries;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class ApiActionEndpointTests
{
    private readonly List<object> _commands = [];

    [TestMethod]
    public async Task Cancel_is_204_and_records_the_signed_in_user()
    {
        await using var app = await StartAsync();

        using var response = await PostAsync(app, "/api/jobs/7/cancel");

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual(new CancelJob(new JobId(7), "local"), _commands.Single());
    }

    [TestMethod]
    public async Task Pause_and_resume_are_204_and_record_the_signed_in_user()
    {
        await using var app = await StartAsync();

        using var paused = await PostAsync(app, "/api/jobs/7/pause");
        using var resumed = await PostAsync(app, "/api/jobs/7/resume");

        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (paused.StatusCode, resumed.StatusCode));
        CollectionAssert.AreEqual(new object[] { new PauseJob(new JobId(7), "local"), new ResumeJob(new JobId(7), "local") }, _commands);
    }

    [TestMethod]
    public async Task A_permission_request_is_answered_once_with_a_known_choice()
    {
        await using var app = await StartAsync();

        using var allowed = await PostAsync(app, "/api/jobs/7/permissions/3", new { choice = "repo" });
        using var late = await PostAsync(app, "/api/jobs/7/permissions/4", new { choice = "deny" });
        using var unknown = await PostAsync(app, "/api/jobs/7/permissions/3", new { choice = "1" });

        Assert.AreEqual(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.AreEqual(new PermissionAnswer(new JobId(7), "repo", "local", 3), _commands[0]);
        Assert.AreEqual(HttpStatusCode.Conflict, late.StatusCode);
        Assert.AreEqual("already_decided", (await ProblemAsync(late)).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.HasCount(2, _commands, "an unknown choice never reaches the handler");
    }

    [TestMethod]
    public async Task Remembered_approvals_are_listed_and_an_admin_revokes_them()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var rules = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/permissions/rules", UriKind.Relative))).RootElement;
        using var revoked = await (await new AntiforgeryClient(app).InitAsync()).SendAsync(HttpMethod.Delete, "/api/permissions/rules/5");
        using var gone = await (await new AntiforgeryClient(app).InitAsync()).SendAsync(HttpMethod.Delete, "/api/permissions/rules/6");

        CollectionAssert.AreEqual(new[] { "id", "repo", "jobId", "ruleKey", "createdBy", "createdAt" }, rules[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual(JsonValueKind.Null, rules[0].GetProperty("jobId").ValueKind, "a repository-wide rule");
        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (revoked.StatusCode, gone.StatusCode));
        Assert.AreEqual(new RevokePermissionRule(5, "local"), _commands[0]);
    }

    [TestMethod]
    public async Task An_invalid_transition_is_a_409_problem_with_its_code()
    {
        await using var app = await StartAsync();

        using var response = await PostAsync(app, "/api/jobs/8/retry");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.AreEqual("invalid_transition", problem.GetProperty("code").GetString());
        Assert.AreEqual("Cannot retry from state Done.", problem.GetProperty("detail").GetString());
    }

    [TestMethod]
    public async Task A_web_message_is_accepted_and_submitted_without_a_provider()
    {
        await using var app = await StartAsync();

        using var response = await PostAsync(app, "/api/jobs/7/messages", new { text = "  use v2  " });

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("resumed", (await ProblemAsync(response)).GetProperty("outcome").GetString());
        Assert.AreEqual(new SubmitDeveloperMessage(new JobId(7), "use v2", "local"), _commands.OfType<SubmitDeveloperMessage>().Single(), "Via = null mirrors it to every chat thread");
        Assert.AreEqual(new AnswerCloseOut(new JobId(7), "use v2"), _commands.OfType<AnswerCloseOut>().Single(), "routed like a chat reply: the close-out question first");
    }

    [TestMethod]
    public async Task Messages_are_validated_and_refused_by_finished_jobs()
    {
        await using var app = await StartAsync();

        using var empty = await PostAsync(app, "/api/jobs/7/messages", new { text = " " });
        using var huge = await PostAsync(app, "/api/jobs/7/messages", new { text = new string('x', 8_001) });
        using var finished = await PostAsync(app, "/api/jobs/8/messages", new { text = "hello?" });

        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, finished.StatusCode);
        Assert.AreEqual("not_accepted", (await ProblemAsync(finished)).GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task The_hand_off_starts_like_the_handoff_command()
    {
        await using var app = await StartAsync();

        using var started = await PostAsync(app, "/api/jobs/7/handoff");
        using var tooEarly = await PostAsync(app, "/api/jobs/8/handoff");

        Assert.AreEqual((HttpStatusCode.Accepted, HttpStatusCode.BadRequest), (started.StatusCode, tooEarly.StatusCode));
        Assert.AreEqual(new StartHandoff(new JobId(7)), _commands[0]);
        Assert.AreEqual("The hand-off starts after the PR is merged.", (await ProblemAsync(tooEarly)).GetProperty("detail").GetString());
    }

    [TestMethod]
    public async Task Running_a_work_item_queues_a_job_like_the_run_command()
    {
        await using var app = await StartAsync();

        using var response = await PostAsync(app, "/api/workitems/5613/run");
        using var active = await PostAsync(app, "/api/workitems/1/run");
        using var bad = await PostAsync(app, "/api/workitems/0/run");

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("/api/jobs/42", response.Headers.Location?.ToString());
        Assert.AreEqual(42, (await ProblemAsync(response)).GetProperty("jobId").GetInt64());
        Assert.AreEqual(new ClaimWorkItem(WorkItemId.From(5613), Force: true), _commands[0]);
        Assert.AreEqual(HttpStatusCode.Conflict, active.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [TestMethod]
    public async Task The_diff_returns_the_view_model_or_404()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var diff = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/jobs/7/diff", UriKind.Relative))).RootElement;
        using var missing = await client.GetAsync(new Uri("/api/jobs/9/diff", UriKind.Relative));

        CollectionAssert.AreEqual(new[] { "baseRef", "headRef", "files", "unifiedDiff", "truncated" }, diff.EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual("AGENTS.md", diff.GetProperty("files")[0].GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual("not_found", (await ProblemAsync(missing)).GetProperty("code").GetString());
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<ICommandHandler<CancelJob, Unit>>(Handler<CancelJob, Unit>(_ => Unit.Value));
        builder.Services.AddSingleton<ICommandHandler<PauseJob, Unit>>(Handler<PauseJob, Unit>(_ => Unit.Value));
        builder.Services.AddSingleton<ICommandHandler<ResumeJob, Unit>>(Handler<ResumeJob, Unit>(_ => Unit.Value));
        builder.Services.AddSingleton<ICommandHandler<RetryJob, int>>(Handler<RetryJob, int>(c => DomainError.InvalidTransition("Done", "retry")));
        builder.Services.AddSingleton<ICommandHandler<SubmitDeveloperMessage, DeveloperMessageOutcome>>(Handler<SubmitDeveloperMessage, DeveloperMessageOutcome>(c =>
            c.JobId.Value == 7 ? DeveloperMessageOutcome.Resumed : DeveloperMessageOutcome.NotAccepted));
        builder.Services.AddSingleton<ICommandHandler<AnswerCloseOut, bool>>(Handler<AnswerCloseOut, bool>(_ => false));
        builder.Services.AddSingleton<ICommandHandler<StartHandoff, Unit>>(Handler<StartHandoff, Unit>(c =>
            c.JobId.Value == 7 ? Unit.Value : DomainError.Validation("The hand-off starts after the PR is merged.")));
        builder.Services.AddSingleton<JobMessages>();
        builder.Services.AddSingleton<ICommandHandler<ClaimWorkItem, JobId>>(Handler<ClaimWorkItem, JobId>(c =>
            c.WorkItemId.Value == 1 ? DomainError.Conflict("Work item 1 already has active job 3.") : new JobId(42)));
        builder.Services.AddSingleton<ICommandHandler<PermissionAnswer, bool>>(Handler<PermissionAnswer, bool>(c => c.RequestId == 3));
        builder.Services.AddSingleton<ICommandHandler<RevokePermissionRule, Unit>>(Handler<RevokePermissionRule, Unit>(c =>
            c.RuleId == 5 ? Unit.Value : DomainError.NotFound($"Permission rule {c.RuleId}")));
        builder.Services.AddSingleton<IQueryHandler<GetPermissionRules, IReadOnlyList<PermissionRule>>>(new Rules());
        builder.Services.AddSingleton<IQueryHandler<GetJobDiff, Result<BranchDiff>>>(new Diffs());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private Command<TCommand, TResult> Handler<TCommand, TResult>(Func<TCommand, Result<TResult>> answer)
        where TCommand : notnull => new(answer, _commands);

    private static async Task<HttpResponseMessage> PostAsync(WebApplication app, string url, object? body = null) =>
        await (await new AntiforgeryClient(app).InitAsync()).PostAsync(url, body);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class Command<TCommand, TResult>(Func<TCommand, Result<TResult>> answer, List<object> seen) : ICommandHandler<TCommand, TResult>
        where TCommand : notnull
    {
        public Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
        {
            seen.Add(command);
            return Task.FromResult(answer(command));
        }
    }

    private sealed class Rules : IQueryHandler<GetPermissionRules, IReadOnlyList<PermissionRule>>
    {
        public Task<IReadOnlyList<PermissionRule>> Handle(GetPermissionRules query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PermissionRule>>([new PermissionRule(5, "sysmin", null, "Bash(npm install:*)", "tngo", DateTimeOffset.UnixEpoch)]);
    }

    private sealed class Diffs : IQueryHandler<GetJobDiff, Result<BranchDiff>>
    {
        public Task<Result<BranchDiff>> Handle(GetJobDiff query, CancellationToken cancellationToken) =>
            Task.FromResult<Result<BranchDiff>>(query.JobId.Value == 7
                ? new BranchDiff("origin/develop", "ai/5613-refine", ["AGENTS.md"], "diff --git a/AGENTS.md b/AGENTS.md\n", false)
                : DomainError.NotFound($"Job {query.JobId}"));
    }
}
