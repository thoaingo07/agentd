using Microsoft.AspNetCore.SignalR;

namespace Agentd.Bff.Hubs;

/// <summary>
/// Live events for the browser at <c>/hubs/events</c>. Read-only: clients subscribe to <c>"all"</c>
/// (summary events of every job) or to a job id (its full stream), passing the last <c>seq</c> they
/// have. The server sends <c>event(stream, EventVm)</c>, replaying what was missed before going live.
/// Every state change goes through REST, which has antiforgery protection.
/// </summary>
public sealed class EventsHub(EventStreams streams) : Hub
{
    public const string Path = "/hubs/events";

    /// <summary>The client method that receives events: <c>(string stream, EventVm event)</c>.</summary>
    public const string EventMethod = "event";

    public Task Subscribe(string stream, long afterSeq) => streams.SubscribeAsync(Context.ConnectionId, stream, afterSeq, Context.ConnectionAborted);

    public Task Unsubscribe(string stream)
    {
        streams.Unsubscribe(Context.ConnectionId, stream);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        streams.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
