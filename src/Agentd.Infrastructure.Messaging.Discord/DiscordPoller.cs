using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>
/// Receives Discord messages without the Gateway: every <see cref="DiscordOptions.PollInterval"/> it
/// reads new messages in each open job thread and idea thread (replies and commands) and in the parent channel
/// (commands only), and hands them to <see cref="IInboundMessageSink"/>. At startup each thread's
/// recent messages are replayed so replies sent while agentd was down are picked up; inbound dedupe
/// makes the replay safe. The parent channel starts from its newest message, so old commands never run.
/// </summary>
public sealed partial class DiscordPoller(
    DiscordRest rest,
    DiscordMessagingProvider provider,
    IServiceScopeFactory scopes,
    IOptions<DiscordOptions> options,
    ILogger<DiscordPoller> logger) : BackgroundService
{
    private const int PageSize = 50;
    private readonly ConcurrentDictionary<string, ulong> _lastSeen = new();
    private bool _parentInitialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPollFailed(logger, ex);
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass over the parent channel, every open job thread and every open idea thread.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!_parentInitialized)
        {
            var newest = await rest.GetAsync($"channels/{o.ChannelId}/messages?limit=1", ct).ConfigureAwait(false);
            _lastSeen[o.ChannelId] = newest?.AsArray().Select(m => DiscordInbound.Snowflake(m!)).DefaultIfEmpty(0UL).Max() ?? 0;
            _parentInitialized = true;
        }

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var sink = scope.ServiceProvider.GetRequiredService<IInboundMessageSink>();
            await PollChannelAsync(o.ChannelId, commandsOnly: true, sink, ct).ConfigureAwait(false);
            var jobThreads = await scope.ServiceProvider.GetRequiredService<IConversationStore>().ListOpenAsync(provider.Key, ct).ConfigureAwait(false);
            // Idea threads live in the ideas table, not conversations (an idea has no job yet).
            var ideaThreads = scope.ServiceProvider.GetService<IIdeaStore>() is { } ideas
                ? await ideas.ListOpenThreadsAsync(provider.Key, ct).ConfigureAwait(false)
                : [];
            var reviewThreads = scope.ServiceProvider.GetService<Application.Reviews.IReviewStore>() is { } reviews
                ? await reviews.ListOpenThreadsAsync(provider.Key, ct).ConfigureAwait(false)
                : [];
            var chatThreads = scope.ServiceProvider.GetService<Application.Chats.IChatStore>() is { } chats
                ? await chats.ListOpenThreadsAsync(provider.Key, ct).ConfigureAwait(false)
                : [];
            foreach (var thread in jobThreads.Select(c => c.ExternalConversationId).Concat(ideaThreads).Concat(reviewThreads).Concat(chatThreads).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await PollChannelAsync(thread, commandsOnly: false, sink, ct).ConfigureAwait(false);
                }
                catch (MessagingDeliveryException ex)
                {
                    // A deleted thread or a lost permission must not stop the other threads.
                    LogThreadFailed(logger, thread, ex.Message);
                }
            }
        }
    }

    private async Task PollChannelAsync(string channelId, bool commandsOnly, IInboundMessageSink sink, CancellationToken ct)
    {
        var after = _lastSeen.TryGetValue(channelId, out var last) && last > 0
            ? string.Create(CultureInfo.InvariantCulture, $"&after={last}")
            : "";
        var page = await rest.GetAsync($"channels/{channelId}/messages?limit={PageSize}{after}", ct).ConfigureAwait(false);
        var messages = page?.AsArray().OfType<JsonNode>().OrderBy(DiscordInbound.Snowflake).ToList() ?? [];
        foreach (var message in messages)
        {
            var inbound = DiscordInbound.Map(message, channelId, options.Value.CommandPrefix, provider.LastOptions(channelId));
            if (inbound is not null && (!commandsOnly || inbound.Command is not null))
            {
                await sink.HandleAsync(inbound, ct).ConfigureAwait(false);
            }

            // Advance only after the message was handled: a failure retries it next pass.
            _lastSeen[channelId] = DiscordInbound.Snowflake(message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling Discord failed; retrying")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling Discord thread {ThreadId} failed: {Reason}")]
    private static partial void LogThreadFailed(ILogger logger, string threadId, string reason);
}
