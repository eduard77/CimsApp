using CimsApp.Models;

namespace CimsApp.Services.Notifications;

/// <summary>
/// Persists a <see cref="Notification"/> row and pushes it to any
/// SignalR client currently connected on behalf of <c>UserId</c>. The
/// hub-push side is fire-and-forget for the persistence path: an
/// offline user still gets a row in the table and the next /api call
/// surfaces it. Used by the threshold evaluator (T-S14-04) and any
/// future inline call sites that want to notify a user.
/// </summary>
public interface INotificationPusher
{
    Task PushAsync(
        Guid userId,
        string type,
        string title,
        string body,
        string? link = null,
        CancellationToken ct = default);
}
