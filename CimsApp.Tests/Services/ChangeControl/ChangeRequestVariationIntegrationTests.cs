using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.ChangeControl;

/// <summary>
/// End-to-end integration tests for the ChangeRequest ↔ Variation
/// link (T-S5-06, PAFM-SD F.6 fifth bullet — "Linked to variations").
/// The spawn itself lives in <see cref="ChangeRequestService.ApproveAsync"/>
/// (T-S5-04) when the caller passes <c>CreateVariation = true</c>.
/// These tests exercise the cross-service guarantee: the spawned
/// Variation is a fully-fledged S1 entity that flows through the
/// existing <see cref="VariationsService"/> workflow, and the
/// ChangeRequest's GeneratedVariationId remains stable as the
/// Variation transitions on its own.
/// </summary>
public class ChangeRequestVariationIntegrationTests
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

    private static RaiseChangeRequestRequest BasicRaise(
        decimal? costImpact = null, int? timeImpactDays = null) =>
        new(Title: "Add basement parking",
            Description: "Client variation request",
            Category: ChangeRequestCategory.Scope,
            BsaCategory: BsaHrbCategory.NotApplicable,
            ProgrammeImpactSummary: null, CostImpactSummary: null,
            EstimatedCostImpact: costImpact,
            EstimatedTimeImpactDays: timeImpactDays);

    private static async Task<Guid> RaiseAndAssessAsync(
        ChangeRequestService svc, Guid projectId, Guid userId,
        decimal? costImpact = null, int? timeImpactDays = null)
    {
        var id = (await svc.RaiseAsync(projectId,
            BasicRaise(costImpact, timeImpactDays), userId)).Id;
        await svc.AssessAsync(projectId, id,
            new AssessChangeRequestRequest("Impact assessed",
                null, null, null, null, null),
            userId, UserRole.InformationManager);
        return id;
    }

    [Fact]
    public async Task Approve_with_CreateVariation_carries_impacts_to_Variation()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid crId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var crSvc = new ChangeRequestService(db, new AuditService(db));
            crId = await RaiseAndAssessAsync(crSvc, projectId, userId,
                costImpact: 75000m, timeImpactDays: 14);
            await crSvc.ApproveAsync(projectId, crId,
                new ApproveChangeRequestRequest("Client signed off", CreateVariation: true),
                userId, UserRole.ProjectManager);
        }

        using var verify = new CimsDbContext(options, tenant);
        var cr = await verify.ChangeRequests.SingleAsync(c => c.Id == crId);
        Assert.NotNull(cr.GeneratedVariationId);
        var v = await verify.Variations.SingleAsync(x => x.Id == cr.GeneratedVariationId);
        Assert.Equal("VAR-0001",                v.VariationNumber);
        Assert.Contains("CR-0001",              v.Title);
        Assert.Equal(75000m,                    v.EstimatedCostImpact);
        Assert.Equal(14,                        v.EstimatedTimeImpactDays);
        Assert.Equal(VariationState.Raised,     v.State);
    }

    [Fact]
    public async Task Spawned_Variation_can_be_Approved_via_VariationsService()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid crId, varId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var crSvc = new ChangeRequestService(db, new AuditService(db));
            crId = await RaiseAndAssessAsync(crSvc, projectId, userId, costImpact: 10000m);
            await crSvc.ApproveAsync(projectId, crId,
                new ApproveChangeRequestRequest("OK", CreateVariation: true),
                userId, UserRole.ProjectManager);
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var cr = await db.ChangeRequests.SingleAsync(c => c.Id == crId);
            varId = cr.GeneratedVariationId!.Value;

            // Drive the spawned Variation through the existing S1 workflow.
            var varSvc = new VariationsService(db, new AuditService(db));
            await varSvc.ApproveAsync(projectId, varId,
                new VariationDecisionRequest("Independent approval"),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var v = await verify.Variations.SingleAsync(x => x.Id == varId);
        Assert.Equal(VariationState.Approved,        v.State);
        Assert.Equal("Independent approval",          v.DecisionNote);

        // The CR-side state is still Approved (Variation transition is
        // independent — CR moves to Implemented when site has actioned).
        var cr2 = await verify.ChangeRequests.SingleAsync(c => c.Id == crId);
        Assert.Equal(ChangeRequestState.Approved,    cr2.State);
        Assert.Equal(varId,                          cr2.GeneratedVariationId);
    }

    [Fact]
    public async Task Spawned_Variation_can_be_Rejected_independently_of_CR_state()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid crId, varId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var crSvc = new ChangeRequestService(db, new AuditService(db));
            crId = await RaiseAndAssessAsync(crSvc, projectId, userId);
            await crSvc.ApproveAsync(projectId, crId,
                new ApproveChangeRequestRequest("OK", CreateVariation: true),
                userId, UserRole.ProjectManager);
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var cr = await db.ChangeRequests.SingleAsync(c => c.Id == crId);
            varId = cr.GeneratedVariationId!.Value;
            var varSvc = new VariationsService(db, new AuditService(db));
            await varSvc.RejectAsync(projectId, varId,
                new VariationDecisionRequest("Cost impact too high after re-cost"),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var v = await verify.Variations.SingleAsync(x => x.Id == varId);
        Assert.Equal(VariationState.Rejected, v.State);
        // CR is still Approved — the FK link survives.
        var cr2 = await verify.ChangeRequests.SingleAsync(c => c.Id == crId);
        Assert.Equal(varId, cr2.GeneratedVariationId);
    }

    [Fact]
    public async Task Approve_without_CreateVariation_does_not_spawn_Variation()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid crId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var crSvc = new ChangeRequestService(db, new AuditService(db));
            crId = await RaiseAndAssessAsync(crSvc, projectId, userId, costImpact: 5000m);
            await crSvc.ApproveAsync(projectId, crId,
                new ApproveChangeRequestRequest("OK", CreateVariation: false),
                userId, UserRole.ProjectManager);
        }

        using var verify = new CimsDbContext(options, tenant);
        var cr = await verify.ChangeRequests.SingleAsync(c => c.Id == crId);
        Assert.Null(cr.GeneratedVariationId);
        var variationCount = await verify.Variations.CountAsync(v => v.ProjectId == projectId);
        Assert.Equal(0, variationCount);
        // No change_request.variation_created audit event.
        var spawnAudit = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "change_request.variation_created");
        Assert.Equal(0, spawnAudit);
    }

    [Fact]
    public async Task Multiple_approvals_with_CreateVariation_yield_distinct_VAR_numbers()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var varNumbers = new List<string>();
        using (var db = new CimsDbContext(options, tenant))
        {
            var crSvc = new ChangeRequestService(db, new AuditService(db));
            for (var i = 0; i < 3; i++)
            {
                var id = await RaiseAndAssessAsync(crSvc, projectId, userId, costImpact: i * 1000m);
                await crSvc.ApproveAsync(projectId, id,
                    new ApproveChangeRequestRequest($"Approved #{i}", CreateVariation: true),
                    userId, UserRole.ProjectManager);
            }
        }

        using var verify = new CimsDbContext(options, tenant);
        var variations = await verify.Variations
            .Where(v => v.ProjectId == projectId)
            .OrderBy(v => v.VariationNumber).ToListAsync();
        Assert.Equal(3, variations.Count);
        Assert.Equal("VAR-0001", variations[0].VariationNumber);
        Assert.Equal("VAR-0002", variations[1].VariationNumber);
        Assert.Equal("VAR-0003", variations[2].VariationNumber);
    }
}
