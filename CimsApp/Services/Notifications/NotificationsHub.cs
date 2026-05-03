using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CimsApp.Services.Notifications;

/// <summary>
/// SignalR hub that streams in-app notifications to the connected
/// user. T-S14-02 / PAFM-SD F.14 first bullet.
///
/// Authentication: bearer JWT, with the WebSocket / Server-Sent
/// Events transports reading the token from the
/// <c>access_token</c> query string parameter (configured in
/// <c>Program.cs</c> via <c>JwtBearerEvents.OnMessageReceived</c>).
/// The hub adds each connection to a per-user group named
/// <c>"user:{UserId}"</c>; <see cref="NotificationPusher"/> sends
/// to that group when a new notification is created.
///
/// No client → server methods in v1.0; the only flow is server-push.
/// </summary>
[Authorize]
public class NotificationsHub : Hub
{
    public const string PushMethod = "notification";

    public override Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(sub, out var userId))
        {
            Groups.AddToGroupAsync(Context.ConnectionId, GroupName(userId));
        }
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var sub = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(sub, out var userId))
        {
            Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(userId));
        }
        return base.OnDisconnectedAsync(exception);
    }

    public static string GroupName(Guid userId) => $"user:{userId}";
}
