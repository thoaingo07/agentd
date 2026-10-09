using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Agentd.Mcp.Tests;

[TestClass]
public sealed class McpEndpointTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    [TestMethod]
    public async Task No_token_is_401()
    {
        await using var host = await McpTestHost.StartAsync();

        using var response = await host.Http.SendAsync(Post(null));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task A_revoked_or_unknown_token_is_401()
    {
        await using var host = await McpTestHost.StartAsync();
        var token = host.Tokens.Issue(new JobId(1));
        host.Tokens.Revoke(new JobId(1));

        using var revoked = await host.Http.SendAsync(Post(token));
        using var unknown = await host.Http.SendAsync(Post("not-a-token"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [TestMethod]
    public async Task A_browser_request_is_403_even_with_a_valid_token()
    {
        await using var host = await McpTestHost.StartAsync();
        var request = Post(host.Tokens.Issue(new JobId(1)));
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await host.Http.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task The_agent_sees_exactly_the_agentd_tools()
    {
        await using var host = await McpTestHost.StartAsync();
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(1)));

        var tools = await client.ListToolsAsync();

        CollectionAssert.AreEquivalent(new[] { "finish", "report_progress", "get_work_item", "ask_developer", "set_phase", "submit_plan", "propose_knowledge", "permission" }, tools.Select(t => t.Name).ToList());
        var permission = tools.Single(t => t.Name == "permission").JsonSchema.GetRawText();
        foreach (var argument in new[] { "\"tool_name\"", "\"input\"", "\"tool_use_id\"" })
        {
            StringAssert.Contains(permission, argument, "the CLI's permission prompt arguments");
        }

        var finish = tools.Single(t => t.Name == "finish");
        var schema = finish.JsonSchema.GetRawText();
        Assert.DoesNotContain("\"user\"", schema, "the caller identity is never a tool argument");
        Assert.DoesNotContain("claims", schema);
        CollectionAssert.AreEquivalent(
            new[] { "prTitle", "prDescription", "summary" },
            finish.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList());
    }

    [TestMethod]
    public async Task A_chat_sees_only_the_read_only_azure_devops_tools_and_a_job_never_does()
    {
        await using var host = await McpTestHost.StartAsync();
        await using var chat = await host.ClientAsync(host.Tokens.IssueChat(7));
        await using var job = await host.ClientAsync(host.Tokens.Issue(new JobId(1)));

        var chatTools = (await chat.ListToolsAsync()).Select(t => t.Name).ToList();
        var jobTools = (await job.ListToolsAsync()).Select(t => t.Name).ToList();

        CollectionAssert.AreEquivalent(new[] { "ado_search_work_items", "ado_get_work_item", "ado_list_pull_requests", "ado_get_pull_request", "ado_list_pipelines", "ado_list_builds", "ado_get_build" }, chatTools);
        Assert.IsFalse(jobTools.Any(t => t.StartsWith("ado_", StringComparison.Ordinal)), "a job agent gets no chat tools");
    }

    [TestMethod]
    public async Task A_chat_token_cant_call_a_job_tool_but_can_search()
    {
        await using var host = await McpTestHost.StartAsync();
        await using var chat = await host.ClientAsync(host.Tokens.IssueChat(7));

        var finish = await Task.Run(async () =>
        {
            try
            {
                var r = await chat.CallToolAsync("finish", Args(("prTitle", "T"), ("prDescription", "D"), ("summary", "S")));
                return r.IsError == true ? "refused" : "ran";
            }
            catch (McpException)
            {
                return "refused";
            }
        });
        var search = await chat.CallToolAsync("ado_search_work_items", Args(("state", "Active")));

        Assert.AreEqual("refused", finish, "a chat can't finish (or touch) a job");
        StringAssert.Contains(((TextContentBlock)search.Content[0]).Text, "#5617 [User Story] Active · Deploy to AKS (Active) · Dev One · tags: ai-workflow");
    }

    [TestMethod]
    public async Task A_chat_reads_a_failed_run_with_its_errors_and_log_end()
    {
        await using var host = await McpTestHost.StartAsync();
        await using var chat = await host.ClientAsync(host.Tokens.IssueChat(7));

        var runs = await chat.CallToolAsync("ado_list_builds", Args(("result", "failed")));
        var run = await chat.CallToolAsync("ado_get_build", Args(("id", 901)));
        var missing = await chat.CallToolAsync("ado_get_build", Args(("id", 5)));

        Assert.AreEqual("901 · sysmin-ci 20261008.3 · failed · develop · Dev One · individualCI · a1b2c3d · 2026-10-08 09:06Z", ((TextContentBlock)runs.Content[0]).Text);
        var text = ((TextContentBlock)run.Content[0]).Text;
        StringAssert.Contains(text, "✗ Task \"dotnet test\": failed");
        StringAssert.Contains(text, "  error: Process completed with exit code 1.");
        StringAssert.Contains(text, "    Failed Deploy_ready_probe [12 ms]");
        StringAssert.Contains(((TextContentBlock)missing.Content[0]).Text, "doesn't exist");
    }

    [TestMethod]
    public async Task Chat_and_job_tokens_dont_open_each_others_doors()
    {
        await using var host = await McpTestHost.StartAsync();
        var chat = host.Tokens.IssueChat(7);
        var job = host.Tokens.Issue(new JobId(7));

        Assert.IsNull(host.Tokens.Validate(chat), "a chat token isn't a job token");
        Assert.IsNull(host.Tokens.ValidateChat(job));
        host.Tokens.RevokeChat(7);
        Assert.IsNull(host.Tokens.ValidateChat(chat));
        Assert.IsNotNull(host.Tokens.Validate(job), "revoking the chat leaves job 7 alone");
    }

    [TestMethod]
    public async Task Finish_publishes_the_job_behind_the_token()
    {
        await using var host = await McpTestHost.StartAsync();
        host.RunningJob(id: 42, workItem: 1234);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(42)));

        var result = await client.CallToolAsync("finish", Args(("prTitle", "Fix login"), ("prDescription", "Details"), ("summary", "Done")));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        StringAssert.Contains(Text(result), "pullrequest/42");
        Assert.AreEqual(JobState.Done, (await host.Jobs.GetAsync(new JobId(42), default))!.State);
    }

    [TestMethod]
    public async Task A_token_can_only_act_on_its_own_job()
    {
        await using var host = await McpTestHost.StartAsync();
        host.RunningJob(id: 1, workItem: 100);
        host.RunningJob(id: 2, workItem: 200);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(1)));

        await client.CallToolAsync("finish", Args(("prTitle", "T"), ("prDescription", "D"), ("summary", "S")));

        Assert.AreEqual(JobState.Done, (await host.Jobs.GetAsync(new JobId(1), default))!.State);
        Assert.AreEqual(JobState.Running, (await host.Jobs.GetAsync(new JobId(2), default))!.State, "job B is untouched");
    }

    [TestMethod]
    public async Task Finish_on_a_job_that_is_not_running_is_a_tool_error()
    {
        await using var host = await McpTestHost.StartAsync();
        host.RunningJob(id: 5, workItem: 500);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(5)));
        await client.CallToolAsync("finish", Args(("prTitle", "T"), ("prDescription", "D"), ("summary", "S")));

        var second = await CallAllowingErrorAsync(client, "finish", Args(("prTitle", "T"), ("prDescription", "D"), ("summary", "S")));

        Assert.IsTrue(second.IsError ?? false);
        StringAssert.Contains(Text(second), "finish failed");
    }

    [TestMethod]
    public async Task Report_progress_records_an_event_and_get_work_item_renders_it()
    {
        await using var host = await McpTestHost.StartAsync();
        host.RunningJob(id: 9, workItem: 900);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(9)));

        await client.CallToolAsync("report_progress", Args(("message", "Reproduced the bug")));
        var item = await client.CallToolAsync("get_work_item");

        var progress = host.Events.Appended.Single(e => e.Type == "progress.reported");
        Assert.AreEqual(9L, progress.JobId);
        StringAssert.Contains(progress.Payload, "Reproduced the bug");
        var update = host.Outbox.Enqueued.Single();
        Assert.AreEqual(9L, update.JobId);
        Assert.AreEqual("⏳ Reproduced the bug", update.Message.Message.Markdown, "progress is a visible message; the heartbeat owns the status line");
        StringAssert.Contains(Text(item), "# 900: Fix login");
        StringAssert.Contains(Text(item), "Redirects once.");
    }

    [TestMethod]
    public async Task Ask_developer_puts_the_job_in_waiting_for_human()
    {
        await using var host = await McpTestHost.StartAsync();
        host.RunningJob(id: 11, workItem: 1100);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(11)));

        var result = await client.CallToolAsync("ask_developer", new Dictionary<string, object?> { ["question"] = "v1 or v2?", ["options"] = new[] { "v1", "v2" } });

        StringAssert.Contains(Text(result), "End your turn now");
        Assert.AreEqual(JobState.WaitingForHuman, (await host.Jobs.GetAsync(new JobId(11), default))!.State);
    }

    [TestMethod]
    [DataRow("", 0)]
    [DataRow("ok?", 6)]
    public async Task Ask_developer_validates_the_question_and_options(string question, int options)
    {
        await using var host = await McpTestHost.StartAsync();
        host.RunningJob(id: 12, workItem: 1200);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(12)));

        var result = await CallAllowingErrorAsync(client, "ask_developer",
            new Dictionary<string, object?> { ["question"] = question, ["options"] = Enumerable.Range(1, options).Select(i => $"o{i}").ToArray() });

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(Text(result), "ask_developer failed");
        Assert.AreEqual(JobState.Running, (await host.Jobs.GetAsync(new JobId(12), default))!.State);
    }

    [TestMethod]
    public async Task Unread_developer_messages_come_back_with_tool_results_and_block_finish()
    {
        await using var host = await McpTestHost.StartAsync();
        var job = host.RunningJob(id: 13, workItem: 1300);
        job.ResumeWith("confirm the plan with me first", "tngo");
        await host.Jobs.SaveAsync(job, default);
        await using var client = await host.ClientAsync(host.Tokens.Issue(new JobId(13)));

        var finish = await CallAllowingErrorAsync(client, "finish", Args(("prTitle", "T"), ("prDescription", "D"), ("summary", "S")));
        Assert.IsTrue(finish.IsError ?? false);
        StringAssert.Contains(Text(finish), "the developer sent messages you haven't seen");
        StringAssert.Contains(Text(finish), "- tngo: confirm the plan with me first");
        Assert.AreEqual(JobState.Running, (await host.Jobs.GetAsync(new JobId(13), default))!.State);

        var again = await CallAllowingErrorAsync(client, "finish", Args(("prTitle", "T"), ("prDescription", "D"), ("summary", "S")));
        Assert.IsFalse(again.IsError ?? false, "delivered once; the next finish goes through");

        var other = host.RunningJob(id: 14, workItem: 1400);
        other.ResumeWith("also update the README", "tngo");
        await host.Jobs.SaveAsync(other, default);
        await using var second = await host.ClientAsync(host.Tokens.Issue(new JobId(14)));
        var progress = await second.CallToolAsync("report_progress", Args(("message", "Reading the code")));
        StringAssert.Contains(Text(progress), "- tngo: also update the README");
        Assert.IsEmpty((await host.Jobs.GetAsync(new JobId(14), default))!.PendingMessages);
    }

    private static async Task<CallToolResult> CallAllowingErrorAsync(ModelContextProtocol.Client.McpClient client, string tool, IReadOnlyDictionary<string, object?> args)
    {
        try
        {
            return await client.CallToolAsync(tool, args);
        }
        catch (McpException ex)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static HttpRequestMessage Post(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
