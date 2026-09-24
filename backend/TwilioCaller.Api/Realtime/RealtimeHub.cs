using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Services;

namespace TwilioCaller.Api.Realtime;

/// <summary>
/// Pushes inbound SMS and call notifications to connected apps. Twilio delivers these
/// over webhooks, which an app cannot receive directly, so the backend relays them.
/// Every client joins a group scoped to its user's Twilio connection id, so one
/// account's traffic is never visible to another.
/// </summary>
[Authorize]
public class RealtimeHub(AppDbContext db) : Hub
{
    public static string GroupFor(string connectionId) => $"conn:{connectionId}";

    public override async Task OnConnectedAsync()
    {
        // Webhooks address events by Twilio connection id, so the group is keyed the
        // same way; the token only carries the user, and the connection is looked up.
        var userId = Context.User?.FindFirst(SessionTokenService.UserIdClaim)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            var connectionId = await db.Connections
                .Where(c => c.UserId == userId)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(connectionId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(connectionId));
            }
        }

        await base.OnConnectedAsync();
    }
}
