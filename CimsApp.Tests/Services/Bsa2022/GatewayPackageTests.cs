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

namespace CimsApp.Tests.Services.Bsa2022;

/// <summary>
/// Behavioural tests for T-S10-03: GatewayPackage state machine
/// + service. // BSA 2022 ref: Part 3 (HRB construction).
/// </summary>
public class GatewayPackageTests
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
            IsHrb = true, HrbCategory = BsaHrbCategory.A,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static GatewayPackageService NewSvc(DbContextOptions<CimsDbContext> options, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        return new GatewayPackageService(db, new AuditService(db));
    }

    [Fact]
    public void Workflow_only_allows_Drafting_to_Submitted_to_Decided()
    {
        Assert.True(GatewayPackageWorkflow.IsValidTransition(
            GatewayPackageState.Drafting, GatewayPackageState.Submitted));
        Assert.True(GatewayPackageWorkflow.IsValidTransition(
            GatewayPackageState.Submitted, GatewayPackageState.Decided));
        // Skipping a state is invalid.
        Assert.False(GatewayPackageWorkflow.IsValidTransition(
            GatewayPackageState.Drafting, GatewayPackageState.Decided));
        // Decided is terminal.
        Assert.False(GatewayPackageWorkflow.IsValidTransition(
            GatewayPackageState.Decided, GatewayPackageState.Submitted));
        Assert.True(GatewayPackageWorkflow.IsTerminal(GatewayPackageState.Decided));
    }

    [Fact]
    public async Task CreateAsync_assigns_per_type_sequential_number()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);

        var g1a = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway1, "G1 first", null),
            userId, null, null);
        var g1b = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway1, "G1 second", null),
            userId, null, null);
        var g2 = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway2, "G2 first", null),
            userId, null, null);

        Assert.Equal("GW1-0001", g1a.Number);
        Assert.Equal("GW1-0002", g1b.Number);
        Assert.Equal("GW2-0001", g2.Number);
    }

    [Fact]
    public async Task SubmitAsync_moves_state_and_records_submitter()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        var pkg = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway2, "Pre-construction", null),
            userId, null, null);

        var submitted = await svc.SubmitAsync(projectId, pkg.Id,
            userId, UserRole.ProjectManager, null, null);

        Assert.Equal(GatewayPackageState.Submitted, submitted.State);
        Assert.Equal(userId, submitted.SubmittedById);
        Assert.NotNull(submitted.SubmittedAt);
    }

    [Fact]
    public async Task SubmitAsync_rejects_TaskTeamMember_role()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        var pkg = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway1, "Planning", null),
            userId, null, null);

        // Submit role floor is InformationManager+.
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.SubmitAsync(projectId, pkg.Id, userId, UserRole.TaskTeamMember, null, null));
    }

    [Fact]
    public async Task DecideAsync_records_decision_and_decision_note()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        var pkg = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway2, "Pre-con", null),
            userId, null, null);
        await svc.SubmitAsync(projectId, pkg.Id, userId, UserRole.ProjectManager, null, null);

        var decided = await svc.DecideAsync(projectId, pkg.Id,
            new DecideGatewayPackageRequest(GatewayDecision.ApprovedWithConditions,
                "Subject to fire-strategy revision Rev C"),
            userId, UserRole.ProjectManager, null, null);

        Assert.Equal(GatewayPackageState.Decided, decided.State);
        Assert.Equal(GatewayDecision.ApprovedWithConditions, decided.Decision);
        Assert.Contains("Rev C", decided.DecisionNote);
    }

    [Fact]
    public async Task DecideAsync_requires_decision_note()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        var pkg = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway2, "x", null), userId, null, null);
        await svc.SubmitAsync(projectId, pkg.Id, userId, UserRole.ProjectManager, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.DecideAsync(projectId, pkg.Id,
                new DecideGatewayPackageRequest(GatewayDecision.Approved, ""),
                userId, UserRole.ProjectManager, null, null));
    }

    [Fact]
    public async Task DecideAsync_rejects_skip_from_Drafting()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var svc = NewSvc(options, tenant);
        var pkg = await svc.CreateAsync(projectId,
            new CreateGatewayPackageRequest(GatewayType.Gateway2, "x", null), userId, null, null);

        // Drafting cannot transition straight to Decided.
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.DecideAsync(projectId, pkg.Id,
                new DecideGatewayPackageRequest(GatewayDecision.Approved, "ok"),
                userId, UserRole.ProjectManager, null, null));
    }

    [Fact]
    public async Task GetAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _) = BuildFixture();
        var otherTenant = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        var svc = NewSvc(options, otherTenant);

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.GetAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
