using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TwilioCaller.Api.Services;

namespace TwilioCaller.Api.Realtime;

/// <summary>
/// Pushes inbound SMS and call notifications to connected apps. Twilio delivers these
/// over webhooks, which an app cannot receive directly, so the backend relays them.
/// Every client joins a group scoped to its Twilio connection id, so one account's
/// traffic is never visible to another.
/// </summary>
[Authorize]
public class RealtimeHub : Hub
{
    public static string GroupFor(string connectionId) => $"conn:{connectionId}";

    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.User?.FindFirst(SessionTokenService.ConnectionIdClaim)?.Value;
        if (!string.IsNullOrEmpty(connectionId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(connectionId));
        }

        await base.OnConnectedAsync();
    }
}
