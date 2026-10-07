using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Agentd.Application.Setup;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Claude;

/// <summary>The setup step's checks, run with the Claude Code CLI (<c>Agentd:Claude:Binary</c>).</summary>
public sealed class ClaudeCliProbe(IOptions<ClaudeOptions> options) : IClaudeProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public const string Expected = "agentd-ok";

    public async Task<ClaudeLogin> StatusAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var (exit, version) = await RunAsync(o.Binary, ["--version"], Environment(o, token: null), cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return new ClaudeLogin(false, null, false, null, null);
        }

        var (statusExit, output) = await RunAsync(o.Binary, ["auth", "status", "--json"], Environment(o, token: null), cancellationToken).ConfigureAwait(false);
        var status = statusExit == 0 ? Parse(output) : null;
        return new ClaudeLogin(
            true,
            version.Trim(),
            status?["loggedIn"]?.GetValue<bool>() == true,
            status?["authMethod"]?.GetValue<string>(),
            status?["subscriptionType"]?.GetValue<string>());
    }

    public async Task<StepCheck> TestAsync(string? token, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var watch = Stopwatch.StartNew();
        var (exit, output) = await RunAsync(o.Binary,
            ["-p", $"Reply with exactly: {Expected}", "--max-turns", "1", "--model", "haiku", "--output-format", "json", "--strict-mcp-config", "--tools", ""],
            Environment(o, token), cancellationToken).ConfigureAwait(false);
        if (exit == -1)
        {
            return new StepCheck(false, $"'{o.Binary}' isn't installed on this server.", "install Claude Code: npm install -g @anthropic-ai/claude-code");
        }

        var reply = exit == 0 ? Parse(output)?["result"]?.GetValue<string>() : null;
        if (reply?.Contains(Expected, StringComparison.Ordinal) == true)
        {
            return new StepCheck(true, string.Create(CultureInfo.InvariantCulture, $"Claude answered a test prompt in {watch.Elapsed.TotalSeconds:0.0} s ({(token is null ? "the server's login" : "the token")})."));
        }

        var detail = (reply ?? output).Trim().ReplaceLineEndings(" ");
        return new StepCheck(false, $"The test prompt failed: {detail[..Math.Min(200, detail.Length)]}", token is null
            ? "log in on the server (claude auth login), or paste a token from claude setup-token"
            : "create a new token with claude setup-token; a usage limit also shows up here");
    }

    /// <summary>The agents' environment (no daemon secrets); the token given, or none at all for the server's login.</summary>
    private static Dictionary<string, string> Environment(ClaudeOptions o, string? token)
    {
        var env = SafeEnvironment.Build(System.Environment.GetEnvironmentVariables(), o);
        env.Remove(o.OAuthTokenVariable);
        if (!string.IsNullOrWhiteSpace(token))
        {
            env[o.OAuthTokenVariable] = token;
        }

        return env;
    }

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Runs the CLI with exactly <paramref name="env"/>; exit code -1 when it can't be started.</summary>
    private static async Task<(int Exit, string Output)> RunAsync(string binary, string[] args, Dictionary<string, string> env, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment.Clear();
        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (1, "no answer within a minute");
            }

            return (process.ExitCode, process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            return (-1, string.Empty);
        }
    }
}
