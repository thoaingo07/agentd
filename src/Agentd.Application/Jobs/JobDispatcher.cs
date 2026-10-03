using System.Collections.Concurrent;
using System.Threading.Channels;
using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// Starts queued jobs up to <see cref="SchedulerOptions.MaxConcurrent"/>, runs each agent in the
/// background and reports its exit through <see cref="HandleAgentExit"/>. On shutdown, agents that are
/// still running are killed but their jobs stay <see cref="JobState.Running"/>, so startup recovery
/// resumes them in the same session.
/// </summary>
public sealed partial class JobDispatcher : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IAgentRunner _runner;
    private readonly SchedulerOptions _options;
    private readonly ILogger<JobDispatcher> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _runs = new();
    private readonly ConcurrentDictionary<long, Task> _active = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile bool _stopping;

    public JobDispatcher(IServiceScopeFactory scopes, IAgentRunner runner, IOptions<SchedulerOptions> options, ILogger<JobDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopes = scopes;
        _runner = runner;
        _options = options.Value;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrent));
    }

    /// <summary>Identifies this daemon in <c>locked_by</c> when dequeuing.</summary>
    public string Worker { get; } = $"{Environment.MachineName}:{Environment.ProcessId}";

    public int ActiveCount => _active.Count;

    /// <summary>Wakes the scheduler (e.g. a work item was just claimed).</summary>
    public void Signal() => _wake.Writer.TryWrite(true);

    /// <summary>Waits for a signal or until <paramref name="delay"/> passes.</summary>
    public async Task WaitForWorkAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(delay);
        try
        {
            await _wake.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Delay elapsed.
        }
    }

    /// <summary>
    /// Waits for a free slot, then starts a resume turn for a job with queued replies, or dequeues and
    /// starts the next job. False when nothing was runnable
    /// (the caller should wait); true when a job started or failed while preparing (try again at once).
    /// </summary>
    public async Task<bool> TryStartNextAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        Result<AgentRunRequest?> started;
        try
        {
            if (_stopping)
            {
                _slots.Release();
                return false;
            }

            var scope = _scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                // Replies waiting for a running job go first: that job already holds a worktree and a session.
                started = await scope.ServiceProvider.GetRequiredService<ICommandHandler<ResumeJobTurn, AgentRunRequest?>>()
                    .Handle(new ResumeJobTurn(_active.Keys.ToList()), cancellationToken).ConfigureAwait(false);
                if (started is { IsSuccess: true, Value: null })
                {
                    started = await scope.ServiceProvider.GetRequiredService<ICommandHandler<StartNextJob, AgentRunRequest?>>()
                        .Handle(new StartNextJob(Worker), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }

        if (started is { IsSuccess: true, Value: { } request })
        {
            Launch(request);
            return true;
        }

        _slots.Release();
        if (!started.IsSuccess)
        {
            LogPrepareFailed(_logger, started.Error.Message);
            return true;
        }

        return false;
    }

    /// <summary>Runs a resume request from startup recovery once a slot is free.</summary>
    public async Task ResumeAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_stopping)
        {
            _slots.Release();
            return;
        }

        Launch(request);
    }

    /// <summary>Stops taking jobs, waits up to the shutdown grace, then kills the remaining agents.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        var running = Task.WhenAll(_active.Values.ToArray());
        if (await Task.WhenAny(running, Task.Delay(_options.ShutdownGrace)).ConfigureAwait(false) != running)
        {
            LogKilling(_logger, _active.Count);
            await _runs.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _runs.Dispose();
        _slots.Dispose();
    }

    private void Launch(AgentRunRequest request)
    {
        // Registered before it starts, so the finally block never runs ahead of the registration.
        var run = new Task<Task>(() => RunAsync(request));
        _active[request.JobId.Value] = run.Unwrap();
        run.Start(TaskScheduler.Default);
    }

    private async Task RunAsync(AgentRunRequest request)
    {
        try
        {
            AgentRunOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(request, _runs.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(_logger, ex, request.JobId.Value);
                outcome = new AgentRunOutcome.Exited(-1, null);
            }

            if (_runs.IsCancellationRequested)
            {
                LogLeftForRecovery(_logger, request.JobId.Value);
                return;
            }

            var scope = _scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<HandleAgentExit, JobState>>()
                    .Handle(new HandleAgentExit(request.JobId, outcome), CancellationToken.None).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    LogExited(_logger, request.JobId.Value, outcome.GetType().Name, result.Value);
                }
                else
                {
                    LogExitFailed(_logger, request.JobId.Value, result.Error.Message);
                }
            }
        }
        catch (Exception ex)
        {
            LogRunFailed(_logger, ex, request.JobId.Value);
        }
        finally
        {
            _active.TryRemove(request.JobId.Value, out _);
            _slots.Release();
            Signal();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing a job failed: {Reason}")]
    private static partial void LogPrepareFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId}: agent run failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, long jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: agent ended ({Outcome}); job is {State}")]
    private static partial void LogExited(ILogger logger, long jobId, string outcome, JobState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: handling the agent exit failed: {Reason}")]
    private static partial void LogExitFailed(ILogger logger, long jobId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId}: agent stopped by shutdown; it resumes on the next start")]
    private static partial void LogLeftForRecovery(ILogger logger, long jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown grace elapsed; killing {Count} running agent(s)")]
    private static partial void LogKilling(ILogger logger, int count);
}
