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
/// Behavioural tests for <see cref="StakeholdersService"/> (T-S3-03).
/// Covers Create / Update / Deactivate lifecycle, audit-twin
/// emission, validation rules, the Mendelow auto-compute path, and
/// the cross-tenant query-filter 404.
/// </summary>
public class StakeholdersServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
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
        return (options, tenant, orgId, userId, projectId);
    }

    private static CreateStakeholderRequest Basic(int p = 4, int i = 5) =>
        new(Name: "Local council planner",
            Organisation: "Borough Council",
            Role: "Planning officer",
            Email: "planner@council.example",
            Phone: "+44 1234 567890",
            Power: p,
            Interest: i,
            EngagementApproach: null,
            EngagementNotes: "Quarterly briefing + invite to design panel");

    [Theory]
    [InlineData(5, 5, EngagementApproach.ManageClosely)]
    [InlineData(3, 3, EngagementApproach.ManageClosely)]
    [InlineData(5, 1, EngagementApproach.KeepSatisfied)]
    [InlineData(4, 2, EngagementApproach.KeepSatisfied)]
    [InlineData(1, 5, EngagementApproach.KeepInformed)]
    [InlineData(2, 4, EngagementApproach.KeepInformed)]
    [InlineData(1, 1, EngagementApproach.Monitor)]
    [InlineData(2, 2, EngagementApproach.Monitor)]
    public void ComputeApproach_maps_Mendelow_quadrants_at_3_midpoint(int p, int i, EngagementApproach expected)
    {
        Assert.Equal(expected, StakeholdersService.ComputeApproach(p, i));
    }

    [Fact]
    public async Task CreateAsync_persists_with_Score_and_auto_computed_approach()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.Stakeholders.SingleAsync(x => x.Id == id);
        Assert.Equal(20, s.Score);                                   // 4 × 5
        Assert.Equal(EngagementApproach.ManageClosely, s.EngagementApproach);
        Assert.Equal("Local council planner", s.Name);
    }

    [Fact]
    public async Task CreateAsync_caller_can_override_auto_computed_approach()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId,
                Basic() with { EngagementApproach = EngagementApproach.Monitor },
                userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.Stakeholders.SingleAsync(x => x.Id == id);
        Assert.Equal(EngagementApproach.Monitor, s.EngagementApproach);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(6, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 6)]
    public async Task CreateAsync_rejects_power_or_interest_out_of_range(int p, int i)
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new StakeholdersService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic(p, i), userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_name()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new StakeholdersService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic() with { Name = "  " }, userId));
    }

    [Fact]
    public async Task CreateAsync_emits_stakeholder_created_audit_twin()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.CreateAsync(projectId, Basic(), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "stakeholder.created");
        Assert.Equal("Stakeholder", row.Entity);
        Assert.Contains("Local council planner", row.Detail!);
        Assert.Contains("\"score\":20", row.Detail);
        Assert.Contains("ManageClosely", row.Detail);
    }

    [Fact]
    public async Task UpdateAsync_partial_update_recomputes_Score_and_Approach()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.UpdateAsync(projectId, id,
                new UpdateStakeholderRequest(
                    null, null, null, null, null,
                    Power: 1, Interest: null,
                    null, null),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.Stakeholders.SingleAsync(x => x.Id == id);
        Assert.Equal(1, s.Power);                                 // changed
        Assert.Equal(5, s.Interest);                              // unchanged
        Assert.Equal(5, s.Score);                                 // recomputed
        // (Power=1, Interest=5) → KeepInformed via auto-recompute
        Assert.Equal(EngagementApproach.KeepInformed, s.EngagementApproach);
    }

    [Fact]
    public async Task UpdateAsync_explicit_Approach_wins_over_auto_recompute()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.UpdateAsync(projectId, id,
                new UpdateStakeholderRequest(
                    null, null, null, null, null,
                    Power: 1, Interest: 1,
                    EngagementApproach: EngagementApproach.ManageClosely,  // override
                    null),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.Stakeholders.SingleAsync(x => x.Id == id);
        Assert.Equal(1, s.Score);  // P=1×I=1
        // Auto-recompute would say Monitor for (1,1), but caller overrode.
        Assert.Equal(EngagementApproach.ManageClosely, s.EngagementApproach);
    }

    [Fact]
    public async Task UpdateAsync_rejects_no_op_change()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateStakeholderRequest(null, null, null, null, null, null, null, null, null),
                userId));
    }

    [Fact]
    public async Task UpdateAsync_rejects_already_deactivated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateStakeholderRequest(Name: "New", null, null, null, null, null, null, null, null),
                userId));
    }

    [Fact]
    public async Task DeactivateAsync_sets_IsActive_false_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.Stakeholders.SingleAsync(x => x.Id == id);
        Assert.False(s.IsActive);

        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "stakeholder.deactivated");
        Assert.Equal("Stakeholder", row.Entity);
        Assert.Contains("Local council planner", row.Detail!);
    }

    [Fact]
    public async Task DeactivateAsync_rejects_already_deactivated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.DeactivateAsync(projectId, id, userId));
    }

    [Fact]
    public async Task ListAsync_returns_active_stakeholders_ordered_by_score_desc()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            await svc.CreateAsync(projectId, Basic(2, 2) with { Name = "Low" }, userId);
            await svc.CreateAsync(projectId, Basic(5, 5) with { Name = "High" }, userId);
            var midId = (await svc.CreateAsync(projectId, Basic(3, 3) with { Name = "Mid" }, userId)).Id;
            // Deactivate one to confirm listing skips inactive rows.
            await svc.DeactivateAsync(projectId, midId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new StakeholdersService(db2, new AuditService(db2));
        var list = await svc2.ListAsync(projectId);

        Assert.Equal(2, list.Count);
        Assert.Equal("High", list[0].Name);   // 25
        Assert.Equal("Low",  list[1].Name);   // 4
    }

    [Fact]
    public async Task UpdateAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new StakeholdersService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
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
            svc2.UpdateAsync(projectId, id,
                new UpdateStakeholderRequest(Name: "Pwn", null, null, null, null, null, null, null, null),
                attacker.UserId!.Value));
    }
}
