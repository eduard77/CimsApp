using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Audit;
using CimsApp.Services.Notifications;
using CimsApp.Tests.TestDoubles;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Notifications;

/// <summary>
/// T-S14-02 NotificationPusher behavioural tests. The pusher
/// persists a Notification row AND fans out to the user's SignalR
/// group; tests cover the persistence path with a fake hub context
/// that records group sends.
/// </summary>
public class NotificationPusherTests
{
    [Fact]
    public async Task PushAsync_writes_row_and_fans_to_user_group()
    {
        var (db, hub, userId) = Build();
        var pusher = new NotificationPusher(db, hub);

        await pusher.PushAsync(userId, "alert.threshold",
            "Cost overrun", "Project X exceeded 110% budget",
            link: "/projects/x/cost");

        var row = await db.Notifications.IgnoreQueryFilters()
            .SingleAsync(n => n.UserId == userId);
        Assert.Equal("alert.threshold", row.Type);
        Assert.Equal("Cost overrun",    row.Title);
        Assert.False(row.Read);
        Assert.NotNull(row.Body);

        Assert.Single(hub.Sends);
        var (group, method, _) = hub.Sends[0];
        Assert.Equal(NotificationsHub.GroupName(userId), group);
        Assert.Equal(NotificationsHub.PushMethod, method);
    }

    [Fact]
    public async Task PushAsync_persists_even_when_user_offline()
    {
        // The hub fan-out is fire-and-forget: an offline user (no
        // active connection in the group) still gets the row, so the
        // next /api/v1/notifications GET surfaces it.
        var (db, hub, userId) = Build();
        var pusher = new NotificationPusher(db, hub);

        await pusher.PushAsync(userId, "alert.threshold",
            "T", "B");

        Assert.Equal(1, await db.Notifications.IgnoreQueryFilters().CountAsync());
    }

    private static (CimsDbContext db, FakeHubContext hub, Guid userId) Build()
    {
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId,
            GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
            .Options;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "T", LastName = "U",
                OrganisationId = orgId,
            });
            seed.SaveChanges();
        }
        return (new CimsDbContext(options, tenant), new FakeHubContext(), userId);
    }
}
