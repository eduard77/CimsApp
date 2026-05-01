using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Schedule;

/// <summary>
/// Behavioural tests for <see cref="LpsService"/> (T-S4-07). Covers
/// lookahead CRUD, Weekly Work Plan create + listing, commitment
/// add / update with the reason-required guard, the
/// PPC compute-on-read formula, and cross-tenant 404 via the query
/// filter.
/// </summary>
public class LpsServiceTests
{
    private static readonly DateTime Monday20260601 = new(2026, 6, 1);   // Monday
    private static readonly DateTime Wed20260603    = new(2026, 6, 3);   // Wednesday
    private static readonly DateTime Monday20260608 = new(2026, 6, 8);

    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid actA, Guid actB) BuildFixture()
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

        var actA = Guid.NewGuid();
        var actB = Guid.NewGuid();

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
        seed.Activities.AddRange(
            new Activity { Id = actA, ProjectId = projectId, Code = "A", Name = "Act A", Duration = 5m },
            new Activity { Id = actB, ProjectId = projectId, Code = "B", Name = "Act B", Duration = 3m });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, actA, actB);
    }

    // ── Lookahead ───────────────────────────────────────────────────

    [Fact]
    public async Task AddLookaheadAsync_normalises_arbitrary_day_to_Monday()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            // Pass in a Wednesday — service should snap to the Monday.
            id = (await svc.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(actA, Wed20260603, false, null), userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var entry = await verify.LookaheadEntries.SingleAsync(e => e.Id == id);
        Assert.Equal(Monday20260601, entry.WeekStarting);
    }

    [Fact]
    public async Task AddLookaheadAsync_rejects_unknown_activity()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new LpsService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(Guid.NewGuid(), Monday20260601, false, null),
                userId));
    }

    [Fact]
    public async Task UpdateLookaheadAsync_toggles_constraintsRemoved_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            id = (await svc.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(actA, Monday20260601, false, null), userId)).Id;
            await svc.UpdateLookaheadAsync(projectId, id,
                new UpdateLookaheadEntryRequest(ConstraintsRemoved: true, Notes: null), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var entry = await verify.LookaheadEntries.SingleAsync(e => e.Id == id);
        Assert.True(entry.ConstraintsRemoved);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "lookahead.updated");
        Assert.Contains("ConstraintsRemoved", row.Detail!);
    }

    [Fact]
    public async Task RemoveLookaheadAsync_soft_deletes()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            id = (await svc.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(actA, Monday20260601, false, null), userId)).Id;
            await svc.RemoveLookaheadAsync(projectId, id, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var entry = await verify.LookaheadEntries.SingleAsync(e => e.Id == id);
        Assert.False(entry.IsActive);
    }

    [Fact]
    public async Task ListLookaheadAsync_filters_by_week_when_supplied()
    {
        var (options, tenant, _, userId, projectId, actA, actB) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            await svc.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(actA, Monday20260601, false, null), userId);
            await svc.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(actB, Monday20260608, false, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        var weekFiltered = await svc2.ListLookaheadAsync(projectId, Monday20260608);
        Assert.Single(weekFiltered);
        var unfiltered = await svc2.ListLookaheadAsync(projectId, null);
        Assert.Equal(2, unfiltered.Count);
    }

    // ── Weekly Work Plan ────────────────────────────────────────────

    [Fact]
    public async Task CreateWeeklyWorkPlanAsync_persists_and_normalises_week_to_Monday()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            id = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Wed20260603, "First week"), userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var w = await verify.WeeklyWorkPlans.SingleAsync(x => x.Id == id);
        Assert.Equal(Monday20260601, w.WeekStarting);
        Assert.Equal("First week", w.Notes);
    }

    [Fact]
    public async Task CreateWeeklyWorkPlanAsync_rejects_duplicate_week_in_same_project()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Wed20260603, null), userId));
    }

    [Fact]
    public async Task ListWeeklyWorkPlansAsync_returns_newest_week_first()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId);
            await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260608, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        var list = await svc2.ListWeeklyWorkPlansAsync(projectId);
        Assert.Equal(2, list.Count);
        Assert.Equal(Monday20260608, list[0].WeekStarting);
        Assert.Equal(Monday20260601, list[1].WeekStarting);
    }

    // ── Commitments + PPC ───────────────────────────────────────────

    [Fact]
    public async Task AddCommitmentAsync_rejects_duplicate_activity_in_same_wwp()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid wwpId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
            await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, "second-time"), userId));
    }

    [Fact]
    public async Task UpdateCommitmentAsync_rejects_completed_false_without_reason()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid wwpId, commitId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
            commitId = (await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, null), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateCommitmentAsync(projectId, wwpId, commitId,
                new UpdateCommitmentRequest(Completed: false, Reason: null, Notes: null),
                userId));
    }

    [Fact]
    public async Task UpdateCommitmentAsync_accepts_completed_false_with_reason()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid wwpId, commitId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
            commitId = (await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, null), userId)).Id;
            await svc.UpdateCommitmentAsync(projectId, wwpId, commitId,
                new UpdateCommitmentRequest(Completed: false,
                    Reason: LpsReasonForNonCompletion.MaterialDelay,
                    Notes: "Concrete delivery slipped"),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.WeeklyTaskCommitments.SingleAsync(x => x.Id == commitId);
        Assert.False(c.Completed);
        Assert.Equal(LpsReasonForNonCompletion.MaterialDelay, c.Reason);
    }

    [Fact]
    public async Task UpdateCommitmentAsync_completed_true_clears_any_prior_reason()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid wwpId, commitId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
            commitId = (await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, null), userId)).Id;
            await svc.UpdateCommitmentAsync(projectId, wwpId, commitId,
                new UpdateCommitmentRequest(false, LpsReasonForNonCompletion.WeatherImpact, null), userId);
            // Now flip back to completed.
            await svc.UpdateCommitmentAsync(projectId, wwpId, commitId,
                new UpdateCommitmentRequest(Completed: true, Reason: null, Notes: null), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.WeeklyTaskCommitments.SingleAsync(x => x.Id == commitId);
        Assert.True(c.Completed);
        Assert.Null(c.Reason);
    }

    [Fact]
    public async Task GetWeeklyWorkPlanAsync_returns_PPC_compute_on_read()
    {
        var (options, tenant, _, userId, projectId, actA, actB) = BuildFixture();
        Guid wwpId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
            var cA = await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, null), userId);
            var cB = await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actB, null), userId);
            // Mark A completed, leave B with a reason for failure.
            await svc.UpdateCommitmentAsync(projectId, wwpId, cA.Id,
                new UpdateCommitmentRequest(true, null, null), userId);
            await svc.UpdateCommitmentAsync(projectId, wwpId, cB.Id,
                new UpdateCommitmentRequest(false,
                    LpsReasonForNonCompletion.PrerequisiteIncomplete, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        var dto = await svc2.GetWeeklyWorkPlanAsync(projectId, wwpId);
        Assert.Equal(2, dto.CommittedCount);
        Assert.Equal(1, dto.CompletedCount);
        Assert.Equal(50.00m, dto.PercentPlanComplete);    // 1 / 2 × 100
        Assert.Equal(2, dto.Commitments.Count);
    }

    [Fact]
    public async Task GetWeeklyWorkPlanAsync_PPC_null_with_no_commitments()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        Guid wwpId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new LpsService(db2, new AuditService(db2));
        var dto = await svc2.GetWeeklyWorkPlanAsync(projectId, wwpId);
        Assert.Equal(0, dto.CommittedCount);
        Assert.Null(dto.PercentPlanComplete);
    }

    [Fact]
    public async Task RemoveCommitmentAsync_deletes_row_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId, actA, _) = BuildFixture();
        Guid wwpId, commitId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            wwpId = (await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId)).Id;
            commitId = (await svc.AddCommitmentAsync(projectId, wwpId,
                new AddCommitmentRequest(actA, null), userId)).Id;
            await svc.RemoveCommitmentAsync(projectId, wwpId, commitId, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        Assert.False(await verify.WeeklyTaskCommitments.AnyAsync(c => c.Id == commitId));
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "weekly_commitment.removed");
        Assert.NotNull(row.Detail);
    }

    [Fact]
    public async Task CreateWeeklyWorkPlanAsync_cross_tenant_404s()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new LpsService(db, new AuditService(db));
            await svc.CreateWeeklyWorkPlanAsync(projectId,
                new CreateWeeklyWorkPlanRequest(Monday20260601, null), userId);
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new LpsService(db2, new AuditService(db2));
        // Same projectId but cross-tenant — service should 404 the project.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.AddLookaheadAsync(projectId,
                new CreateLookaheadEntryRequest(Guid.NewGuid(), Monday20260601, false, null),
                attacker.UserId!.Value));
    }
}
