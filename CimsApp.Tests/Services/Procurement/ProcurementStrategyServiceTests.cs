using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Procurement;

/// <summary>
/// Behavioural tests for <see cref="ProcurementStrategyService"/>
/// (T-S6-02). Covers upsert semantics (first call creates, second
/// updates), the unique-per-project constraint, the Approve transition,
/// re-approval (v1.0 allows refresh), and cross-tenant 404 via the
/// query filter.
/// </summary>
public class ProcurementStrategyServiceTests
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

    private static UpsertProcurementStrategyRequest Basic(
        ProcurementApproach approach = ProcurementApproach.DesignAndBuild,
        ContractForm contractForm = ContractForm.Nec4OptionA,
        decimal? estimatedValue = 5_000_000m) =>
        new(approach, contractForm, estimatedValue,
            KeyDates: "Tender issue 2026-07-01; Award 2026-09-15",
            PackageBreakdownNotes: "Concrete frame; MEP; Fit-out");

    [Fact]
    public async Task CreateOrUpdateAsync_first_call_creates_strategy()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid strategyId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            strategyId = (await svc.CreateOrUpdateAsync(projectId, Basic(), userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.ProcurementStrategies.SingleAsync(x => x.Id == strategyId);
        Assert.Equal(ProcurementApproach.DesignAndBuild, s.Approach);
        Assert.Equal(ContractForm.Nec4OptionA,           s.ContractForm);
        Assert.Equal(5_000_000m,                          s.EstimatedTotalValue);
        Assert.Null(s.ApprovedById);
        Assert.Null(s.ApprovedAt);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_emits_created_audit_on_first_call_then_updated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            await svc.CreateOrUpdateAsync(projectId, Basic(), userId);
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            await svc.CreateOrUpdateAsync(projectId,
                Basic(approach: ProcurementApproach.Traditional,
                      contractForm: ContractForm.JctStandardBuilding),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var actions = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action.StartsWith("procurement_strategy."))
            .OrderBy(a => a.CreatedAt).ToListAsync();
        Assert.Equal(2, actions.Count);
        Assert.Equal("procurement_strategy.created", actions[0].Action);
        Assert.Equal("procurement_strategy.updated", actions[1].Action);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_second_call_updates_existing_row_not_creates_new()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            await svc.CreateOrUpdateAsync(projectId, Basic(), userId);
            await svc.CreateOrUpdateAsync(projectId,
                Basic(approach: ProcurementApproach.Traditional,
                      contractForm: ContractForm.JctStandardBuilding,
                      estimatedValue: 7_500_000m),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        // Still one row per project — upsert.
        var rows = await verify.ProcurementStrategies
            .Where(s => s.ProjectId == projectId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(ProcurementApproach.Traditional, rows[0].Approach);
        Assert.Equal(ContractForm.JctStandardBuilding, rows[0].ContractForm);
        Assert.Equal(7_500_000m,                       rows[0].EstimatedTotalValue);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_rejects_negative_estimated_value()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ProcurementStrategyService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateOrUpdateAsync(projectId, Basic(estimatedValue: -1m), userId));
    }

    [Fact]
    public async Task CreateOrUpdateAsync_unknown_project_404s()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ProcurementStrategyService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateOrUpdateAsync(Guid.NewGuid(), Basic(), userId));
    }

    [Fact]
    public async Task ApproveAsync_records_approver_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            await svc.CreateOrUpdateAsync(projectId, Basic(), userId);
            await svc.ApproveAsync(projectId, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.ProcurementStrategies.SingleAsync();
        Assert.Equal(userId, s.ApprovedById);
        Assert.NotNull(s.ApprovedAt);

        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "procurement_strategy.approved");
        Assert.Equal("ProcurementStrategy", row.Entity);
    }

    [Fact]
    public async Task ApproveAsync_allows_re_approval_with_refreshed_timestamp()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        DateTime firstApprovedAt;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            await svc.CreateOrUpdateAsync(projectId, Basic(), userId);
            var s1 = await svc.ApproveAsync(projectId, userId);
            firstApprovedAt = s1.ApprovedAt!.Value;
        }

        // Tiny delay to make sure UtcNow advances at least 1 tick.
        await Task.Delay(10);

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            var s2 = await svc.ApproveAsync(projectId, userId);
            Assert.True(s2.ApprovedAt > firstApprovedAt);
        }

        using var verify = new CimsDbContext(options, tenant);
        var approvals = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "procurement_strategy.approved");
        Assert.Equal(2, approvals);
    }

    [Fact]
    public async Task ApproveAsync_unknown_strategy_404s()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ProcurementStrategyService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ApproveAsync(projectId, userId));
    }

    [Fact]
    public async Task GetAsync_returns_null_when_not_yet_captured()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ProcurementStrategyService(db, new AuditService(db));
        var s = await svc.GetAsync(projectId);
        Assert.Null(s);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_cross_tenant_lookup_404s_via_filter()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProcurementStrategyService(db, new AuditService(db));
            await svc.CreateOrUpdateAsync(projectId, Basic(), userId);
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new ProcurementStrategyService(db2, new AuditService(db2));
        // Project is filtered out for the attacker — gets NotFound on
        // the upfront Project check, not a duplicate-row exception.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.CreateOrUpdateAsync(projectId, Basic(), attacker.UserId!.Value));
    }
}
