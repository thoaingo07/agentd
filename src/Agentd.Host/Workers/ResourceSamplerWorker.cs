using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Git;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Workers;

/// <summary>
/// Every <see cref="Interval"/>: each running agent's process tree (CPU, RAM) and the machine into <see cref="JobActivity"/>;
/// worktree sizes every <see cref="DiskInterval"/> (walking a big worktree isn't free).
/// </summary>
internal sealed partial class ResourceSamplerWorker(
    IAgentRunner runner,
    IResourceSampler sampler,
    JobActivity activity,
    IServiceScopeFactory scopes,
    IOptions<GitOptions> git,
    ILogger<ResourceSamplerWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DiskInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<long, long> _worktreeBytes = [];
    private DateTimeOffset _lastDisk = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SampleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var root = GitOptions.Expand(git.Value.WorktreeRoot);
        if (sampler.SampleMachine(Directory.Exists(root) ? root : AppContext.BaseDirectory) is { } machine)
        {
            activity.RecordMachine(machine);
        }

        var roots = runner.ProcessIds();
        if (now - _lastDisk >= DiskInterval)
        {
            _lastDisk = now;
            await MeasureWorktreesAsync(roots.Keys, ct).ConfigureAwait(false);
        }

        foreach (var (job, (cpu, memory)) in sampler.SampleTrees(roots))
        {
            activity.RecordResources(new JobId(job), new JobResources(cpu, memory, _worktreeBytes.TryGetValue(job, out var d) ? d : null, now));
        }
    }

    private async Task MeasureWorktreesAsync(IEnumerable<long> running, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            _worktreeBytes.Clear();
            foreach (var id in running)
            {
                if (await jobs.GetAsync(new JobId(id), ct).ConfigureAwait(false) is { State: JobState.Running, Worktree: { } worktree } && Directory.Exists(worktree.Value))
                {
                    _worktreeBytes[id] = Size(worktree.Value);
                }
            }
        }
    }

    internal static long Size(string folder)
    {
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Deleted meanwhile.
            }
        }

        return bytes;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sampling resources failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
