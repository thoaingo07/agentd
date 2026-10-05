using System.ComponentModel;
using System.Globalization;
using System.Text;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentd.Mcp;

/// <summary>
/// The tools a Claude Code agent uses to talk back to agentd. The job is always the bearer token's job,
/// read from the authenticated HTTP request; it is never a tool argument the agent could set.
/// </summary>
[McpServerToolType]
public sealed class AgentdTools(
    IHttpContextAccessor http,
    ICommandHandler<FinishWork, PullRequestRef> finish,
    IJobRepository jobs,
    IWorkItemSource workItems,
    ICommandHandler<AskDeveloper, Unit> ask,
    ICommandHandler<ReportProgress, Unit> progress,
    ICommandHandler<TakeDeveloperMessages, IReadOnlyList<string>> unread,
    ICommandHandler<SetPhase, Unit> phases,
    ICommandHandler<SubmitPlan, PlanOutcome> plans,
    ICommandHandler<ProposeKnowledge, Unit> knowledge,
    ICommandHandler<Application.Permissions.PermissionAsk, Application.Permissions.PermissionDecision>? permissions = null)
{
    /// <summary>
    /// Claude Code's permission prompt (<c>--permission-prompt-tool mcp__agentd__permission</c>): called by the CLI,
    /// not by the agent, for a tool call outside the allowlist. A person allows or denies it in chat or the Web UI.
    /// Contract (checked against CLI 2.1.289): arguments <c>tool_name</c>, <c>input</c>, <c>tool_use_id</c>; the
    /// result is JSON text, <c>{"behavior":"allow","updatedInput":…}</c> or <c>{"behavior":"deny","message":…}</c>.
    /// </summary>
    [McpServerTool(Name = "permission"), Description("Permission prompt used by Claude Code itself. Agents never call this tool directly.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Parameter names are the CLI's argument names.")]
    public async Task<string> Permission(
        [Description("The tool that needs permission.")] string tool_name,
        [Description("The tool call's input.")] System.Text.Json.JsonElement input,
        [Description("The tool call's id.")] string? tool_use_id,
        CancellationToken cancellationToken)
    {
        _ = tool_use_id;
        if (permissions is null)
        {
            return Reply(false, "agentd can't ask for permission right now.", input);
        }

        var result = await permissions.Handle(new Application.Permissions.PermissionAsk(CurrentJob(), tool_name, input.GetRawText()), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Reply(result.Value.Allowed, result.Value.Message, input) : Reply(false, result.Error.Message, input);
    }

    private static string Reply(bool allowed, string message, System.Text.Json.JsonElement input) =>
        allowed
            ? System.Text.Json.JsonSerializer.Serialize(new { behavior = "allow", updatedInput = input })
            : System.Text.Json.JsonSerializer.Serialize(new { behavior = "deny", message });

    [McpServerTool(Name = "finish"), Description(
        "Call exactly once when the work item is complete and all changes are committed. agentd pushes your branch and " +
        "opens the pull request. After calling this, end your turn.")]
    public async Task<string> Finish(
        [Description("Pull request title: short and specific, e.g. 'Fix login redirect loop (WI-1234)'.")] string prTitle,
        [Description("Pull request description in Markdown: what changed, why, and how it was verified.")] string prDescription,
        [Description("One or two sentences summarizing the outcome for the developer.")] string summary,
        CancellationToken cancellationToken)
    {
        var jobId = CurrentJob();
        var result = await finish.Handle(new FinishWork(jobId, prTitle, prDescription, summary), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? $"Work recorded and pull request opened: {result.Value.Url}. End your turn now."
            : throw new McpException($"finish failed: {result.Error.Message}");
    }

    [McpServerTool(Name = "report_progress"), Description("Report a short progress update for the developer (a sentence, not a log). It replaces your previous update in chat.")]
    public async Task<string> ReportProgress(
        [Description("What you just did or are about to do.")] string message,
        CancellationToken cancellationToken)
    {
        var result = await progress.Handle(new ReportProgress(CurrentJob(), message), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? await WithUnreadAsync("Noted.", cancellationToken).ConfigureAwait(false) : throw new McpException(result.Error.Message);
    }

    [McpServerTool(Name = "set_phase"), Description(
        "Announce the lifecycle phase you are entering, with a summary the developer reads in chat: " +
        "clarify (the spec in your own words + open questions), plan (options considered, the chosen plan, an estimate of time and usage), " +
        "implement (what you're changing), verify (commands run and their results), fix (review feedback being addressed), handoff (knowledge and learnings).")]
    public async Task<string> SetPhase(
        [Description("One of: clarify, plan, implement, verify, fix, handoff.")] string phase,
        [Description("What the developer should know about this phase, in Markdown (up to 3,000 characters).")] string summary,
        CancellationToken cancellationToken)
    {
        var result = await phases.Handle(new SetPhase(CurrentJob(), phase, summary), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? await WithUnreadAsync($"Phase set to {phase}.", cancellationToken).ConfigureAwait(false)
            : throw new McpException($"set_phase failed: {result.Error.Message}");
    }

    [McpServerTool(Name = "submit_plan"), Description(
        "Submit your implementation plan with an estimate. If the developer must approve it, it is posted for approval and you must END YOUR TURN " +
        "(you can't edit files until it's approved; you'll be resumed with their decision). Otherwise it is posted and you continue.")]
    public async Task<string> SubmitPlan(
        [Description("The plan in Markdown: the options you considered, the chosen approach, the files to change, how you'll verify (up to 1,800 characters).")] string plan,
        [Description("Estimated time to implement and verify, in minutes.")] int estimateMinutes,
        [Description("Estimated share of the 5-hour Claude usage window this will take, in percent (0–100).")] int estimateUsagePercent,
        CancellationToken cancellationToken)
    {
        var result = await plans.Handle(new SubmitPlan(CurrentJob(), plan, estimateMinutes, estimateUsagePercent), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new McpException($"submit_plan failed: {result.Error.Message}");
        }

        return result.Value == PlanOutcome.AwaitingApproval
            ? "Plan posted for the developer's approval. End your turn now; you will be resumed with their decision."
            : await WithUnreadAsync("Plan posted. Continue with the implementation.", cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "propose_knowledge"), Description(
        "During the hand-off (after the PR was merged): propose the knowledge and learnings to sync into the repository's docs " +
        "(AGENTS.md, CLAUDE.md, docs/): what you would change and where. Then END YOUR TURN; you'll be resumed with the developer's answer.")]
    public async Task<string> ProposeKnowledge(
        [Description("The proposed changes in Markdown: for each, the file and section, and the new or corrected text (up to 1,800 characters).")] string proposal,
        CancellationToken cancellationToken)
    {
        var result = await knowledge.Handle(new ProposeKnowledge(CurrentJob(), proposal), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? "Proposal posted to the developer. End your turn now; you will be resumed with their answer."
            : throw new McpException($"propose_knowledge failed: {result.Error.Message}");
    }

    [McpServerTool(Name = "ask_developer"), Description(
        "Ask the developer a question when you need a decision you can't make from the work item or the code. " +
        "The question is posted in the job's chat thread. After calling this, END YOUR TURN: you will be resumed with the answer.")]
    public async Task<string> AskDeveloper(
        [Description("The question, with enough context to answer it without opening the code (up to 2,000 characters).")] string question,
        [Description("Optional short answers to offer as buttons (up to 5).")] string[]? options,
        CancellationToken cancellationToken)
    {
        var result = await ask.Handle(new AskDeveloper(CurrentJob(), question, options ?? []), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? "Question posted to the developer. End your turn now; you will be resumed with their answer."
            : throw new McpException($"ask_developer failed: {result.Error.Message}");
    }

    [McpServerTool(Name = "get_work_item"), Description("Get the latest version of your work item (title, description, acceptance criteria, comments).")]
    public async Task<string> GetWorkItem(CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(CurrentJob(), cancellationToken).ConfigureAwait(false)
            ?? throw new McpException("Your job no longer exists.");
        var item = await workItems.GetAsync(job.WorkItemId.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new McpException($"Work item {job.WorkItemId} no longer exists.");

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {item.Id}: {item.Title} ({item.State})");
        void Section(string title, string? body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {title}").AppendLine(body.Trim());
            }
        }

        Section("Description", item.Description);
        Section("Acceptance criteria", item.AcceptanceCriteria);
        Section("Repro steps", item.ReproSteps);
        Section("Comments", string.Join("\n", item.Comments.Select(c => $"- {c.Author} ({c.CreatedAt:yyyy-MM-dd}): {c.Text}")));
        return await WithUnreadAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends the developer's unread messages, so they reach the agent at its next tool call.</summary>
    private async Task<string> WithUnreadAsync(string result, CancellationToken ct)
    {
        var messages = await unread.Handle(new TakeDeveloperMessages(CurrentJob()), ct).ConfigureAwait(false);
        return messages is { IsSuccess: true, Value.Count: > 0 } ? result + TakeDeveloperMessagesHandler.Format(messages.Value) : result;
    }

    private JobId CurrentJob() =>
        http.HttpContext?.User.FindFirst(McpJobAuthenticationHandler.JobIdClaim)?.Value is { } id && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? new JobId(value)
            : throw new McpException("Not authenticated as an agentd job.");
}
