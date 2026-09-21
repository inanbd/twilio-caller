using Microsoft.AspNetCore.SignalR;
using TwilioCaller.Api.Contracts;

namespace TwilioCaller.Api.Realtime;

/// <summary>Fan-out helper so webhook handlers do not depend on SignalR directly.</summary>
public class RealtimeNotifier(IHubContext<RealtimeHub> hub)
{
    public Task MessageReceivedAsync(string connectionId, InboundEventDto evt) =>
        hub.Clients.Group(RealtimeHub.GroupFor(connectionId))
            .SendAsync("messageReceived", evt);

    public Task MessageStatusAsync(string connectionId, string sid, string status) =>
        hub.Clients.Group(RealtimeHub.GroupFor(connectionId))
            .SendAsync("messageStatus", new { sid, status });

    public Task IncomingCallAsync(string connectionId, InboundEventDto evt) =>
        hub.Clients.Group(RealtimeHub.GroupFor(connectionId))
            .SendAsync("incomingCall", evt);

    public Task CallStatusAsync(string connectionId, string sid, string status) =>
        hub.Clients.Group(RealtimeHub.GroupFor(connectionId))
            .SendAsync("callStatus", new { sid, status });
}
