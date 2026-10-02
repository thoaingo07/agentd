using System.Collections.Concurrent;
using Agentd.Application.Messaging;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Agentd.Host.Workers;

/// <summary>
/// Per-provider status for <c>/healthz</c>: each provider's own check (cached 30 s) and whether the
/// dispatcher paused it. A provider that's down is <b>Degraded</b>, never Unhealthy: agentd keeps
/// working and messages wait in the outbox.
/// </summary>
internal sealed class MessagingProviderHealthCheck(IMessagingProviderRegistry providers, OutboxDispatcher dispatcher) : IHealthCheck
{
    private static readonly TimeSpan s_cacheFor = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<ProviderKey, (DateTimeOffset At, ProviderHealth Health)> _cache = new();

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var degraded = new List<string>();
        var paused = dispatcher.PausedProviders;
        foreach (var provider in providers.Enabled)
        {
            var health = await CachedAsync(provider, cancellationToken).ConfigureAwait(false);
            var isPaused = paused.Contains(provider.Key);
            data[provider.Key.Value] = isPaused ? $"paused after repeated send failures; {health.Detail}" : health.Detail;
            if (!health.Healthy || isPaused)
            {
                degraded.Add(provider.Key.Value);
            }
        }

        return degraded.Count == 0
            ? HealthCheckResult.Healthy(data: data)
            : HealthCheckResult.Degraded($"Messaging degraded: {string.Join(", ", degraded)}", data: data);
    }

    private async Task<ProviderHealth> CachedAsync(IMessagingProvider provider, CancellationToken ct)
    {
        if (_cache.TryGetValue(provider.Key, out var cached) && DateTimeOffset.UtcNow - cached.At < s_cacheFor)
        {
            return cached.Health;
        }

        ProviderHealth health;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            health = await provider.CheckHealthAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            health = new ProviderHealth(false, ex.Message);
        }

        _cache[provider.Key] = (DateTimeOffset.UtcNow, health);
        return health;
    }
}
