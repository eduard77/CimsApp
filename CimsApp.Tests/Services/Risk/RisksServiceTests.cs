using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Risk;

/// <summary>
/// Behavioural tests for <see cref="RisksService"/> (T-S2-04).
/// Covers Create / Update / Close lifecycle, audit-twin emission,
/// validation rules, and the tenant query-filter 404 for
/// cross-tenant lookups.
/// </summary>
public class RisksServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid categoryId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
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
        seed.RiskCategories.Add(new RiskCategory
        {
            Id = categoryId, ProjectId = projectId, Code = "1", Name = "Technical",
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, categoryId);
    }

    private static CreateRiskRequest BasicCreate(Guid? categoryId = null) =>
        new(Title: "Foundation design risk",
            Description: "Soil bearing capacity below assumed",
            CategoryId: categoryId,
            Probability: 4,
            Impact: 5,
            OwnerId: null,
            ResponseStrategy: null,
            ResponsePlan: null,
            ContingencyAmount: null);

    [Fact]
    public async Task CreateAsync_persists_risk_with_score_equals_P_times_I()
    {
        var (options, tenant, _, userId, projectId, categoryId) = BuildFixture();

        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            var r = await svc.CreateAsync(projectId, BasicCreate(categoryId), userId);
            riskId = r.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var risk = await verify.Risks.SingleAsync(r => r.Id == riskId);
        Assert.Equal(20, risk.Score);
        Assert.Equal(4, risk.Probability);
        Assert.Equal(5, risk.Impact);
        Assert.Equal(RiskStatus.Identified, risk.Status);
        Assert.Equal(categoryId, risk.CategoryId);
        Assert.Equal(projectId, risk.ProjectId);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(6, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 6)]
    public async Task CreateAsync_rejects_probability_or_impact_out_of_range(int p, int i)
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new RisksService(db, new AuditService(db));

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, BasicCreate() with { Probability = p, Impact = i }, userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_title()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new RisksService(db, new AuditService(db));

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, BasicCreate() with { Title = "   " }, userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_categoryId()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new RisksService(db, new AuditService(db));
        var bogusCategory = Guid.NewGuid();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateAsync(projectId, BasicCreate(bogusCategory), userId));
    }

    [Fact]
    public async Task CreateAsync_emits_risk_created_audit_twin()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.CreateAsync(projectId, BasicCreate(), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var audits = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "risk.created")
            .ToListAsync();
        var created = Assert.Single(audits);
        Assert.Equal("Risk", created.Entity);
        Assert.Equal(userId, created.UserId);
        Assert.Equal(projectId, created.ProjectId);
        Assert.NotNull(created.Detail);
        Assert.Contains("Foundation design risk", created.Detail);
        Assert.Contains("\"score\":20", created.Detail);
    }

    [Fact]
    public async Task UpdateAsync_partial_update_changes_only_specified_fields_and_recomputes_score()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            var r = await svc.CreateAsync(projectId, BasicCreate(), userId);
            riskId = r.Id;
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(
                    Title: null, Description: null, CategoryId: null,
                    Probability: 3, Impact: null,
                    Status: null, OwnerId: null, ResponseStrategy: null,
                    ResponsePlan: null, ContingencyAmount: null),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var risk = await verify.Risks.SingleAsync(r => r.Id == riskId);
        Assert.Equal(3, risk.Probability);     // changed
        Assert.Equal(5, risk.Impact);          // unchanged
        Assert.Equal(15, risk.Score);          // recomputed = 3 × 5
        Assert.Equal("Foundation design risk", risk.Title); // unchanged
    }

    [Fact]
    public async Task UpdateAsync_rejects_setting_status_closed_via_update()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(null, null, null, null, null, RiskStatus.Closed,
                    null, null, null, null), userId));
    }

    [Fact]
    public async Task UpdateAsync_rejects_already_closed_risk()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
            await svc.CloseAsync(projectId, riskId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(Title: "New", null, null, null, null, null,
                    null, null, null, null), userId));
    }

    [Fact]
    public async Task UpdateAsync_rejects_empty_change_set()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(null, null, null, null, null, null,
                    null, null, null, null), userId));
    }

    [Fact]
    public async Task UpdateAsync_emits_risk_updated_audit_twin_with_changed_fields()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(null, null, null, Probability: 2, Impact: 2,
                    null, null, null, null, null), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var update = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "risk.updated")
            .SingleAsync();
        Assert.Equal(userId, update.UserId);
        Assert.NotNull(update.Detail);
        Assert.Contains("\"scoreBefore\":20", update.Detail);
        Assert.Contains("\"scoreAfter\":4",   update.Detail);
        Assert.Contains("Probability", update.Detail);
        Assert.Contains("Impact",      update.Detail);
        Assert.Contains("Score",       update.Detail);
    }

    [Fact]
    public async Task CloseAsync_sets_status_closed_and_emits_risk_closed_audit_twin()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.CloseAsync(projectId, riskId, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var risk = await verify.Risks.SingleAsync(r => r.Id == riskId);
        Assert.Equal(RiskStatus.Closed, risk.Status);

        var closed = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "risk.closed");
        Assert.Equal("Risk", closed.Entity);
        Assert.Equal(userId, closed.UserId);
        Assert.Equal(projectId, closed.ProjectId);
        Assert.NotNull(closed.Detail);
        Assert.Contains("Identified", closed.Detail); // previousStatus
    }

    [Fact]
    public async Task CloseAsync_rejects_already_closed_risk()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
            await svc.CloseAsync(projectId, riskId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.CloseAsync(projectId, riskId, userId));
    }

    [Fact]
    public async Task ListAsync_returns_active_risks_ordered_by_score_desc()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.CreateAsync(projectId, BasicCreate() with { Title = "Low",  Probability = 1, Impact = 1 }, userId);
            await svc.CreateAsync(projectId, BasicCreate() with { Title = "High", Probability = 5, Impact = 5 }, userId);
            await svc.CreateAsync(projectId, BasicCreate() with { Title = "Mid",  Probability = 3, Impact = 3 }, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        var list = await svc2.ListAsync(projectId);

        Assert.Equal(3, list.Count);
        Assert.Equal("High", list[0].Title);   // 25
        Assert.Equal("Mid",  list[1].Title);   // 9
        Assert.Equal("Low",  list[2].Title);   // 1
    }

    [Fact]
    public async Task GetMatrixAsync_returns_25_cells_excluding_closed_risks()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid closedId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.CreateAsync(projectId, BasicCreate() with { Title = "Live A", Probability = 4, Impact = 5 }, userId);
            await svc.CreateAsync(projectId, BasicCreate() with { Title = "Live B", Probability = 4, Impact = 5 }, userId);
            closedId = (await svc.CreateAsync(projectId, BasicCreate() with { Title = "Stale", Probability = 4, Impact = 5 }, userId)).Id;
            await svc.CloseAsync(projectId, closedId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        var cells = await svc2.GetMatrixAsync(projectId);

        Assert.Equal(25, cells.Count);
        var c45 = cells.Single(c => c.Probability == 4 && c.Impact == 5);
        Assert.Equal(2, c45.RiskIds.Count); // closed risk excluded
        Assert.DoesNotContain(closedId, c45.RiskIds);
    }

    [Fact]
    public async Task RecordQualitativeAssessmentAsync_sets_notes_assessor_and_bumps_status_to_Assessed()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.RecordQualitativeAssessmentAsync(projectId, riskId,
                new RecordQualitativeAssessmentRequest("Geotechnical survey confirms soil class C; mitigation: deeper piles."),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var risk = await verify.Risks.SingleAsync(r => r.Id == riskId);
        Assert.Equal(RiskStatus.Assessed, risk.Status);
        Assert.NotNull(risk.QualitativeNotes);
        Assert.Contains("Geotechnical", risk.QualitativeNotes);
        Assert.Equal(userId, risk.AssessedById);
        Assert.NotNull(risk.AssessedAt);

        var auditRow = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "risk.qualitative_assessed");
        // The audit JSON encodes `>` as `>` (default JSON-safe
        // policy); just assert the two endpoints of the transition.
        Assert.Contains("Identified", auditRow.Detail!);
        Assert.Contains("Assessed",   auditRow.Detail!);
    }

    [Fact]
    public async Task RecordQualitativeAssessmentAsync_preserves_status_when_already_past_Identified()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
            // Move past Identified before assessing.
            await svc.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(null, null, null, null, null, RiskStatus.Active,
                    null, null, null, null), userId);
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            await svc.RecordQualitativeAssessmentAsync(projectId, riskId,
                new RecordQualitativeAssessmentRequest("Re-assessed mid-flight"),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var risk = await verify.Risks.SingleAsync(r => r.Id == riskId);
        Assert.Equal(RiskStatus.Active, risk.Status);
        Assert.Contains("Re-assessed mid-flight", risk.QualitativeNotes!);
    }

    [Fact]
    public async Task RecordQualitativeAssessmentAsync_rejects_empty_notes()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.RecordQualitativeAssessmentAsync(projectId, riskId,
                new RecordQualitativeAssessmentRequest("   "), userId));
    }

    [Fact]
    public async Task RecordQualitativeAssessmentAsync_rejects_already_closed_risk()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
            await svc.CloseAsync(projectId, riskId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.RecordQualitativeAssessmentAsync(projectId, riskId,
                new RecordQualitativeAssessmentRequest("late"), userId));
    }

    [Fact]
    public async Task UpdateAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixture();
        Guid riskId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RisksService(db, new AuditService(db));
            riskId = (await svc.CreateAsync(projectId, BasicCreate(), userId)).Id;
        }

        // Attacker tenant has its own org and queries the same DB.
        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new RisksService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.UpdateAsync(projectId, riskId,
                new UpdateRiskRequest(Title: "Pwn", null, null, null, null, null,
                    null, null, null, null), attacker.UserId!.Value));
    }
}
