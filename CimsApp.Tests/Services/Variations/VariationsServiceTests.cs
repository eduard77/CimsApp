using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Variations;

/// <summary>
/// Behavioural tests for <see cref="VariationsService"/> (T-S1-08).
/// Covers the core 3-state machine (Raised → Approved or Rejected),
/// the terminal-state guard, sequential VariationNumber generation,
/// optional CBS line linkage, and the cross-tenant + wrong-project
/// 404 patterns shared with the rest of the cost domain.
/// </summary>
public class VariationsServiceTests
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

    private static RaiseVariationRequest NewRaiseRequest(
        string title = "Add piling to grid line C",
        decimal? cost = 25_000m, int? days = 5,
        Guid? cbsLineId = null) =>
        new(title, "Detailed description", "Client request",
            cost, days, cbsLineId);

    [Fact]
    public async Task Raise_writes_row_with_VAR_0001_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid variationId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new VariationsService(db, new AuditService(db));
            var v = await svc.RaiseAsync(projectId, NewRaiseRequest(), userId);
            variationId = v.Id;
            Assert.Equal("VAR-0001", v.VariationNumber);
            Assert.Equal(VariationState.Raised, v.State);
            Assert.Equal(userId, v.RaisedById);
            Assert.Null(v.DecidedById);
            Assert.Null(v.DecidedAt);
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.Variations.Single(v => v.Id == variationId);
        Assert.Equal(projectId, stored.ProjectId);
        Assert.Equal("Add piling to grid line C", stored.Title);
        Assert.Equal(25_000m, stored.EstimatedCostImpact);
        Assert.Equal(5, stored.EstimatedTimeImpactDays);

        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "variation.raised"));
        Assert.Equal("Variation", audit.Entity);
        Assert.Equal(variationId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Contains("\"number\":\"VAR-0001\"", audit.Detail);
        Assert.Contains("\"costImpact\":25000", audit.Detail);
        Assert.Contains("\"timeImpactDays\":5", audit.Detail);
    }

    [Fact]
    public async Task Raise_assigns_sequential_variation_numbers_per_project()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        var v1 = await svc.RaiseAsync(projectId, NewRaiseRequest("First"), userId);
        var v2 = await svc.RaiseAsync(projectId, NewRaiseRequest("Second"), userId);
        var v3 = await svc.RaiseAsync(projectId, NewRaiseRequest("Third"), userId);

        Assert.Equal("VAR-0001", v1.VariationNumber);
        Assert.Equal("VAR-0002", v2.VariationNumber);
        Assert.Equal("VAR-0003", v3.VariationNumber);
    }

    [Fact]
    public async Task Raise_title_is_required()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RaiseAsync(projectId, NewRaiseRequest(title: "  "), userId));
        Assert.Contains("Title is required", ex.Errors[0]);
    }

    [Fact]
    public async Task Raise_with_CBS_line_in_wrong_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var projectB = Guid.NewGuid();
        Guid lineOnB;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            var line = new CostBreakdownItem
            {
                ProjectId = projectB, Code = "1", Name = "B Root",
            };
            seed.CostBreakdownItems.Add(line);
            seed.SaveChanges();
            lineOnB = line.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RaiseAsync(projectA, NewRaiseRequest(cbsLineId: lineOnB), userId));
    }

    [Fact]
    public async Task Raise_cross_tenant_project_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new VariationsService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RaiseAsync(projectB, NewRaiseRequest(), userA));
    }

    [Fact]
    public async Task Approve_transitions_to_Approved_with_audit_and_decision_metadata()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid variationId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new VariationsService(db, new AuditService(db));
            var v = await svc.RaiseAsync(projectId, NewRaiseRequest(), userId);
            variationId = v.Id;
            await svc.ApproveAsync(projectId, variationId,
                new VariationDecisionRequest("Cost is justified by site conditions"), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.Variations.Single(v => v.Id == variationId);
        Assert.Equal(VariationState.Approved, stored.State);
        Assert.Equal(userId, stored.DecidedById);
        Assert.NotNull(stored.DecidedAt);
        Assert.Equal("Cost is justified by site conditions", stored.DecisionNote);

        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "variation.approved"));
        Assert.Equal(variationId.ToString(), audit.EntityId);
        Assert.Contains("\"decisionNote\":\"Cost is justified by site conditions\"", audit.Detail);
        Assert.Contains("\"costImpact\":25000", audit.Detail);
    }

    [Fact]
    public async Task Reject_transitions_to_Rejected_with_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid variationId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new VariationsService(db, new AuditService(db));
            var v = await svc.RaiseAsync(projectId, NewRaiseRequest(), userId);
            variationId = v.Id;
            await svc.RejectAsync(projectId, variationId,
                new VariationDecisionRequest("Not in scope"), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.Variations.Single(v => v.Id == variationId);
        Assert.Equal(VariationState.Rejected, stored.State);
        Assert.Equal("Not in scope", stored.DecisionNote);

        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "variation.rejected"));
        Assert.Equal(variationId.ToString(), audit.EntityId);
        Assert.Contains("\"decisionNote\":\"Not in scope\"", audit.Detail);
    }

    [Fact]
    public async Task Approve_already_approved_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        var v = await svc.RaiseAsync(projectId, NewRaiseRequest(), userId);
        await svc.ApproveAsync(projectId, v.Id, new VariationDecisionRequest(null), userId);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            svc.ApproveAsync(projectId, v.Id, new VariationDecisionRequest(null), userId));
        Assert.Contains("Approved", ex.Message);
    }

    [Fact]
    public async Task Reject_already_rejected_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        var v = await svc.RaiseAsync(projectId, NewRaiseRequest(), userId);
        await svc.RejectAsync(projectId, v.Id, new VariationDecisionRequest(null), userId);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.RejectAsync(projectId, v.Id, new VariationDecisionRequest(null), userId));
    }

    [Fact]
    public async Task Approve_already_rejected_is_rejected()
    {
        // Both terminal states block any further transition (3-state
        // machine in v1.0). v1.1 / B-016 expands to 6 states; until
        // then, terminal is terminal.
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        var v = await svc.RaiseAsync(projectId, NewRaiseRequest(), userId);
        await svc.RejectAsync(projectId, v.Id, new VariationDecisionRequest(null), userId);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            svc.ApproveAsync(projectId, v.Id, new VariationDecisionRequest(null), userId));
        Assert.Contains("Rejected", ex.Message);
    }

    [Fact]
    public async Task Decide_variation_in_wrong_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var projectB = Guid.NewGuid();
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new VariationsService(db, new AuditService(db));
        var v = await svc.RaiseAsync(projectA, NewRaiseRequest(), userId);
        // Trying to approve through project B's URL must NOT find the
        // variation (the (Id, ProjectId) tuple guard on the lookup).
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ApproveAsync(projectB, v.Id,
                new VariationDecisionRequest(null), userId));
    }
}
