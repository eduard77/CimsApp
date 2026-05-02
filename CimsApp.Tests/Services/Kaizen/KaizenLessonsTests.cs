using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Kaizen;

/// <summary>
/// Behavioural tests for the S12 Kaizen / Lessons Learned
/// module: ImprovementRegister + PDCA workflow,
/// LessonsLearned, OpportunityToImprove. **First non-statutory
/// module since S6** — pure pattern reuse from the workflow
/// modules of S5/S6/S10/S11.
/// </summary>
public class KaizenLessonsTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId, GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
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
            Id = projectId, Name = "P", Code = "TP-1",
            AppointingPartyId = orgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    // ── PDCA workflow ───────────────────────────────────────────────

    [Fact]
    public void PdcaWorkflow_supports_full_state_set()
    {
        Assert.True(PdcaWorkflow.IsValidTransition(PdcaState.Plan,  PdcaState.Do));
        Assert.True(PdcaWorkflow.IsValidTransition(PdcaState.Do,    PdcaState.Check));
        Assert.True(PdcaWorkflow.IsValidTransition(PdcaState.Check, PdcaState.Act));
        Assert.True(PdcaWorkflow.IsValidTransition(PdcaState.Act,   PdcaState.Plan));
        Assert.True(PdcaWorkflow.IsValidTransition(PdcaState.Act,   PdcaState.Closed));
        // Any state can early-close (PM+).
        Assert.True(PdcaWorkflow.IsValidTransition(PdcaState.Plan,  PdcaState.Closed));
        // Closed is terminal.
        Assert.True(PdcaWorkflow.IsTerminal(PdcaState.Closed));
        // Cannot skip stages forward.
        Assert.False(PdcaWorkflow.IsValidTransition(PdcaState.Plan, PdcaState.Check));
    }

    // ── Improvement Register ────────────────────────────────────────

    [Fact]
    public async Task ImprovementService_CreateAsync_starts_in_Plan_with_cycle_1()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new ImprovementRegisterService(db, new AuditService(db));

        var dto = await svc.CreateAsync(projectId,
            new CreateImprovementRequest("Reduce RFI turnaround time",
                "Currently averaging 7 days; target 3", userId),
            userId, null, null);

        Assert.Equal(PdcaState.Plan, dto.State);
        Assert.Equal(1,              dto.CycleNumber);
        Assert.Equal("IMP-0001",     dto.Number);
    }

    [Fact]
    public async Task ImprovementService_full_PDCA_cycle_with_stage_notes()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new ImprovementRegisterService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateImprovementRequest("X", "y", userId), userId, null, null);

        // Plan → Do (notes captured on the OUTGOING stage = Plan).
        dto = await svc.TransitionAsync(projectId, dto.Id,
            PdcaState.Do, "Decided to fast-track via daily RFI standup",
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Equal(PdcaState.Do, dto.State);
        Assert.Contains("standup", dto.PlanNotes);

        // Do → Check.
        dto = await svc.TransitionAsync(projectId, dto.Id,
            PdcaState.Check, "Standup ran for 2 weeks",
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Contains("2 weeks", dto.DoNotes);

        // Check → Act.
        dto = await svc.TransitionAsync(projectId, dto.Id,
            PdcaState.Act, "Avg RFI turnaround dropped to 4 days",
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Contains("4 days", dto.CheckNotes);

        // Act → Closed (PM+).
        dto = await svc.TransitionAsync(projectId, dto.Id,
            PdcaState.Closed, "Embed standup as standing practice",
            userId, UserRole.ProjectManager, null, null);
        Assert.Equal(PdcaState.Closed, dto.State);
        Assert.Contains("standing", dto.ActNotes);
        Assert.Equal(1, dto.CycleNumber);  // Closed without cycle-back.
    }

    [Fact]
    public async Task ImprovementService_cycle_back_increments_CycleNumber_and_clears_stage_notes()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new ImprovementRegisterService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateImprovementRequest("X", "y", userId), userId, null, null);
        dto = await svc.TransitionAsync(projectId, dto.Id, PdcaState.Do,    "p1", userId, UserRole.TaskTeamMember, null, null);
        dto = await svc.TransitionAsync(projectId, dto.Id, PdcaState.Check, "d1", userId, UserRole.TaskTeamMember, null, null);
        dto = await svc.TransitionAsync(projectId, dto.Id, PdcaState.Act,   "c1", userId, UserRole.TaskTeamMember, null, null);

        // Act → Plan (cycle-back).
        var cycled = await svc.TransitionAsync(projectId, dto.Id,
            PdcaState.Plan, "a1: didn't go far enough",
            userId, UserRole.TaskTeamMember, null, null);

        Assert.Equal(PdcaState.Plan, cycled.State);
        Assert.Equal(2,              cycled.CycleNumber);
        // Per-stage notes reset for the new cycle.
        Assert.Null(cycled.PlanNotes);
        Assert.Null(cycled.DoNotes);
        Assert.Null(cycled.CheckNotes);
        Assert.Null(cycled.ActNotes);
    }

    [Fact]
    public async Task ImprovementService_TaskTeamMember_cannot_close()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new ImprovementRegisterService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateImprovementRequest("X", "y", userId), userId, null, null);

        // Closing is PM+; TaskTeamMember cannot.
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.TransitionAsync(projectId, dto.Id, PdcaState.Closed, null,
                userId, UserRole.TaskTeamMember, null, null));
    }

    // ── Lessons Learned ─────────────────────────────────────────────

    [Fact]
    public async Task LessonsLearnedService_CreateAsync_stores_org_scoped_lesson()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new LessonsLearnedService(db, new AuditService(db), tenant);

        var dto = await svc.CreateAsync(
            new CreateLessonLearnedRequest(
                "Always verify ground conditions before piling",
                "Pile rig refused refusal at 3m; ground had a void.",
                "Geotechnical", projectId, "piling,ground-conditions"),
            userId, null, null);

        Assert.Equal("Geotechnical",            dto.Category);
        Assert.Equal(projectId,                 dto.SourceProjectId);
        Assert.Contains("ground-conditions",    dto.TagsCsv);
    }

    [Fact]
    public async Task LessonsLearnedService_ListAsync_filters_by_category_and_tag()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new LessonsLearnedService(db, new AuditService(db), tenant);
        await svc.CreateAsync(new CreateLessonLearnedRequest("L1", "x", "Procurement", null, "tendering"), userId, null, null);
        await svc.CreateAsync(new CreateLessonLearnedRequest("L2", "x", "Procurement", null, "supplier-vetting"), userId, null, null);
        await svc.CreateAsync(new CreateLessonLearnedRequest("L3", "x", "Schedule", null, "tendering"), userId, null, null);

        var byCategory = await svc.ListAsync("Procurement", null);
        Assert.Equal(2, byCategory.Count);
        var byTag = await svc.ListAsync(null, "tendering");
        Assert.Equal(2, byTag.Count);
        var both = await svc.ListAsync("Procurement", "supplier-vetting");
        Assert.Single(both);
        Assert.Equal("L2", both[0].Title);
    }

    [Fact]
    public async Task LessonsLearnedService_UpdateAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new LessonsLearnedService(db, new AuditService(db), tenant);
        var dto = await svc.CreateAsync(new CreateLessonLearnedRequest("X", "y", null, null, null), userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateAsync(dto.Id, new UpdateLessonLearnedRequest(null, null, null, null), userId, null, null));
    }

    // ── Opportunity to Improve ─────────────────────────────────────

    [Fact]
    public async Task OpportunityToImproveService_CreateAsync_normalises_SourceEntityType_to_PascalCase()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new OpportunityToImproveService(db, new AuditService(db));
        var sourceId = Guid.NewGuid();

        var dto = await svc.CreateAsync(projectId,
            new CreateOpportunityToImproveRequest(
                "Standardise RFI templates",
                "Most RFIs are missing the photo attachment field",
                SourceEntityType: "rfi",          // lowercase input
                SourceEntityId: sourceId),
            userId, null, null);

        Assert.Equal("Rfi",     dto.SourceEntityType);  // PascalCased
        Assert.Equal(sourceId,  dto.SourceEntityId);
        Assert.Equal("OFI-0001", dto.Number);
    }

    [Fact]
    public async Task OpportunityToImproveService_CreateAsync_rejects_orphan_source_pair()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new OpportunityToImproveService(db, new AuditService(db));

        // SourceEntityType set, SourceEntityId missing.
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                new CreateOpportunityToImproveRequest("X", "y", "Risk", null),
                userId, null, null));
        // SourceEntityId set, SourceEntityType missing.
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                new CreateOpportunityToImproveRequest("X", "y", null, Guid.NewGuid()),
                userId, null, null));
    }

    [Fact]
    public async Task OpportunityToImproveService_ActionAsync_rejects_already_actioned()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new OpportunityToImproveService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateOpportunityToImproveRequest("X", "y", null, null),
            userId, null, null);
        await svc.ActionAsync(projectId, dto.Id,
            new ActionOpportunityToImproveRequest("Tracked under IMP-0001"),
            userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.ActionAsync(projectId, dto.Id,
                new ActionOpportunityToImproveRequest("retry"),
                userId, null, null));
    }

    [Fact]
    public async Task OpportunityToImproveService_ListAsync_filters_by_actioned_state()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new OpportunityToImproveService(db, new AuditService(db));
        var open1 = await svc.CreateAsync(projectId, new CreateOpportunityToImproveRequest("Open 1", "y", null, null), userId, null, null);
        var open2 = await svc.CreateAsync(projectId, new CreateOpportunityToImproveRequest("Open 2", "y", null, null), userId, null, null);
        var actioned = await svc.CreateAsync(projectId, new CreateOpportunityToImproveRequest("Actioned", "y", null, null), userId, null, null);
        await svc.ActionAsync(projectId, actioned.Id, new ActionOpportunityToImproveRequest(null), userId, null, null);

        var openList = await svc.ListAsync(projectId, actioned: false);
        Assert.Equal(2, openList.Count);
        var actionedList = await svc.ListAsync(projectId, actioned: true);
        Assert.Single(actionedList);
        Assert.Equal("Actioned", actionedList[0].Title);
    }
}
