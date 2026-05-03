using CimsApp.Data;
using CimsApp.Models;
using Microsoft.AspNetCore.SignalR;

namespace CimsApp.Services.Notifications;

/// <summary>
/// Default pusher: writes a Notification row, then fire-and-forget
/// sends it to the user's SignalR group. The DB write uses
/// <c>IgnoreQueryFilters</c> on the User lookup so callers with no
/// tenant context (e.g. background services) can still notify a
/// specific user; the row itself carries no OrganisationId — it's
/// scoped via <c>User.OrganisationId</c> by the Notification global
/// query filter.
/// </summary>
public sealed class NotificationPusher(
    CimsDbContext db,
    IHubContext<NotificationsHub> hub) : INotificationPusher
{
    public async Task PushAsync(
        Guid userId,
        string type,
        string title,
        string body,
        string? link = null,
        CancellationToken ct = default)
    {
        var n = new Notification
        {
            UserId = userId,
            Type   = type,
            Title  = title,
            Body   = body,
            Link   = link,
        };
        db.Notifications.Add(n);
        await db.SaveChangesAsync(ct);

        await hub.Clients
            .Group(NotificationsHub.GroupName(userId))
            .SendAsync(NotificationsHub.PushMethod, new
            {
                id = n.Id,
                type = n.Type,
                title = n.Title,
                body = n.Body,
                link = n.Link,
                createdAt = n.CreatedAt,
            }, ct);
    }
}
