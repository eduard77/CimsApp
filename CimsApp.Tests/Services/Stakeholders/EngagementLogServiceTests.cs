using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Stakeholders;

/// <summary>
/// Behavioural tests for the engagement-log slice of
/// <see cref="StakeholdersService"/> (T-S3-06): RecordEngagementAsync
/// and ListEngagementsAsync. Covers the audit-twin emission, required-
/// summary validation, the 200-row listing cap, ordering, and the
/// cross-tenant 404 via the query filter.
/// </summary>
public class EngagementLogServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid stakeholderId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId,
            UserId         = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var interceptor = new AuditInterceptor(tenant, httpAccessor: null);
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "Project", Code = "PR1",
            AppointingPartyId = orgId, Currency = "GBP",
        });
        seed.SaveChanges();

        Guid stakeholderId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            stakeholderId = svc.CreateAsync(projectId,
                new CreateStakeholderRequest(
                    Name: "Council planner",
                    Organisation: "Borough", Role: "Planner",
                    Email: null, Phone: null,
                    Power: 4, Interest: 5,
                    EngagementApproach: null, EngagementNotes: null),
                userId).GetAwaiter().GetResult().Id;
        }
        return (options, tenant, orgId, userId, projectId, stakeholderId);
    }

    private static RecordEngagementRequest Basic(string summary = "Met to walk the planning conditions") =>
        new(Type: EngagementType.Meeting,
            OccurredAt: new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
            Summary: summary,
            ActionsAgreed: "Send revised drainage drawing by Friday");

    [Fact]
    public async Task RecordEngagementAsync_persists_entry_with_caller_as_recorder()
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        Guid entryId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            entryId = (await svc.RecordEngagementAsync(projectId, stakeholderId,
                Basic(), userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var entry = await verify.EngagementLogs.SingleAsync(g => g.Id == entryId);
        Assert.Equal(stakeholderId, entry.StakeholderId);
        Assert.Equal(projectId,     entry.ProjectId);
        Assert.Equal(userId,        entry.RecordedById);
        Assert.Equal(EngagementType.Meeting, entry.Type);
        Assert.Equal("Met to walk the planning conditions", entry.Summary);
        Assert.Equal("Send revised drainage drawing by Friday", entry.ActionsAgreed);
    }

    [Fact]
    public async Task RecordEngagementAsync_emits_engagement_recorded_audit()
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.RecordEngagementAsync(projectId, stakeholderId, Basic(), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "engagement.recorded");
        Assert.Equal("EngagementLog", row.Entity);
        Assert.Contains(stakeholderId.ToString(), row.Detail!);
        Assert.Contains("Meeting",  row.Detail);
        Assert.Contains("\"hasActions\":true", row.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordEngagementAsync_rejects_empty_summary(string summary)
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new StakeholdersService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RecordEngagementAsync(projectId, stakeholderId,
                Basic(summary), userId));
    }

    [Fact]
    public async Task RecordEngagementAsync_unknown_stakeholder_404s()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new StakeholdersService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RecordEngagementAsync(projectId, Guid.NewGuid(),
                Basic(), userId));
    }

    [Fact]
    public async Task RecordEngagementAsync_cross_tenant_404s_via_query_filter()
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        // Confirm baseline insert succeeds for the right tenant.
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.RecordEngagementAsync(projectId, stakeholderId, Basic(), userId);
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.RecordEngagementAsync(projectId, stakeholderId,
                Basic("Cross-tenant attempt"), attacker.UserId!.Value));
    }

    [Fact]
    public async Task ListEngagementsAsync_returns_newest_first()
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.RecordEngagementAsync(projectId, stakeholderId,
                Basic("Old") with { OccurredAt = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc) },
                userId);
            await svc.RecordEngagementAsync(projectId, stakeholderId,
                Basic("New") with { OccurredAt = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc) },
                userId);
            await svc.RecordEngagementAsync(projectId, stakeholderId,
                Basic("Mid") with { OccurredAt = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc) },
                userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        var list = await svc2.ListEngagementsAsync(projectId, stakeholderId);

        Assert.Equal(3, list.Count);
        Assert.Equal("New", list[0].Summary);
        Assert.Equal("Mid", list[1].Summary);
        Assert.Equal("Old", list[2].Summary);
    }

    [Fact]
    public async Task ListEngagementsAsync_caps_at_200_entries()
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            // Insert 205 entries; the most-recent 200 should come back.
            for (var k = 0; k < 205; k++)
            {
                await svc.RecordEngagementAsync(projectId, stakeholderId,
                    Basic($"entry-{k:000}") with
                    {
                        OccurredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(k),
                    },
                    userId);
            }
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        var list = await svc2.ListEngagementsAsync(projectId, stakeholderId);

        Assert.Equal(200, list.Count);
        // Newest first → entry-204 at the head, entry-005 at the tail.
        Assert.Equal("entry-204", list[0].Summary);
        Assert.Equal("entry-005", list[^1].Summary);
    }

    [Fact]
    public async Task ListEngagementsAsync_unknown_stakeholder_404s()
    {
        var (options, tenant, _, _, projectId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new StakeholdersService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ListEngagementsAsync(projectId, Guid.NewGuid()));
    }

    [Fact]
    public async Task ListEngagementsAsync_cross_tenant_404s_via_query_filter()
    {
        var (options, tenant, _, userId, projectId, stakeholderId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.RecordEngagementAsync(projectId, stakeholderId, Basic(), userId);
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.ListEngagementsAsync(projectId, stakeholderId));
    }
}
