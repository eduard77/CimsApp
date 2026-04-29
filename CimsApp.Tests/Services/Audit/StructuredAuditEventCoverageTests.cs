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

namespace CimsApp.Tests.Services.Audit;

/// <summary>
/// Completes the audit-twin coverage matrix for structured events that
/// previously had no explicit assertion on their action name and basic
/// detail. The audit-twin atomicity refactor (PR #33) changed every
/// audit.WriteAsync call site, so these tests pin the action-name
/// contract for the simpler create/update paths that previously
/// relied on per-row AuditInterceptor coverage alone.
///
/// Covers: project.created, document.created, rfi.created,
/// rfi.responded, action.created, action.updated. The high-stakes
/// events (auth.*, payment_certificate.*, document.state_transition,
/// invitation.created, project.member_added, cbs.*, cost_period.*,
/// commitment.created, actual_cost.recorded, variation.*) are
/// covered in their respective service test files.
/// </summary>
public class StructuredAuditEventCoverageTests
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
        // BeginTransactionAsync is a no-op on in-memory but the
        // default warning behaviour escalates to an exception; we
        // suppress so the test exercises production code shapes that
        // wrap writes in transactions (e.g. DocumentsService).
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "Project", Code = "PR1",
            AppointingPartyId = orgId, Currency = "GBP",
            Members = [new ProjectMember { UserId = userId, Role = UserRole.ProjectManager }],
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    [Fact]
    public async Task ProjectsService_CreateAsync_emits_project_created_audit()
    {
        // Custom fixture — ProjectsService.CreateAsync mints the
        // project itself, so we don't pre-seed one. The tenant has
        // OrganisationId = orgId so the AppointingPartyId must
        // match (non-SuperAdmin path).
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var interceptor = new AuditInterceptor(tenant, httpAccessor: null);
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
            seed.Users.Add(new User
            {
                Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "T", LastName = "U",
                OrganisationId = orgId,
            });
            seed.SaveChanges();
        }

        Guid projectId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ProjectsService(db, new AuditService(db), tenant);
            var p = await svc.CreateAsync(
                new CreateProjectRequest(
                    Name: "New Project", Code: "NEW",
                    Description: null, AppointingPartyId: orgId,
                    StartDate: null, EndDate: null,
                    Location: null, Country: null,
                    Currency: "GBP", BudgetValue: null,
                    Sector: null, Sponsor: null, EirRef: null),
                userId, ip: "203.0.113.5", ua: "ua-proj");
            projectId = p.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "project.created"));
        Assert.Equal("Project", audit.Entity);
        Assert.Equal(projectId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
        Assert.Equal("203.0.113.5", audit.IpAddress);
        Assert.Equal("ua-proj", audit.UserAgent);
    }

    [Fact]
    public async Task DocumentsService_CreateAsync_emits_document_created_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid docId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new DocumentsService(db, new AuditService(db));
            var doc = await svc.CreateAsync(projectId,
                new CreateDocumentRequest(
                    ProjectCode: "PR1", Originator: "ABC",
                    Volume: null, Level: null, DocType: "RP",
                    Role: null, Number: 7,
                    Title: "Spec", Description: null,
                    Type: DocumentType.Report,
                    ContainerId: null, Tags: null),
                userId, ip: "203.0.113.6", ua: "ua-doc");
            docId = doc.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "document.created"));
        Assert.Equal("Document", audit.Entity);
        Assert.Equal(docId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(docId, audit.DocumentId);
        Assert.Equal(userId, audit.UserId);
    }

    [Fact]
    public async Task RfiService_CreateAsync_emits_rfi_created_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid rfiId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RfiService(db, new AuditService(db));
            var r = await svc.CreateAsync(projectId,
                new CreateRfiRequest(
                    Subject: "Question", Description: "Body",
                    Discipline: "AR", Priority: Priority.Medium,
                    AssignedToId: null, DueDate: null),
                userId, ip: "203.0.113.7", ua: "ua-rfi");
            rfiId = r.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "rfi.created"));
        Assert.Equal("Rfi", audit.Entity);
        Assert.Equal(rfiId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
    }

    [Fact]
    public async Task RfiService_RespondAsync_emits_rfi_responded_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid rfiId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new RfiService(db, new AuditService(db));
            var r = await svc.CreateAsync(projectId,
                new CreateRfiRequest(
                    Subject: "Q", Description: "B",
                    Discipline: "AR", Priority: Priority.Medium,
                    AssignedToId: null, DueDate: null),
                userId, null, null);
            rfiId = r.Id;
            await svc.RespondAsync(rfiId, projectId,
                new RespondRfiRequest("Answer", RfiStatus.Closed),
                userId, UserRole.InformationManager,
                ip: "203.0.113.8", ua: "ua-resp");
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "rfi.responded"));
        Assert.Equal("Rfi", audit.Entity);
        Assert.Equal(rfiId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
    }

    [Fact]
    public async Task ActionsService_CreateAsync_emits_action_created_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid actionId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ActionsService(db, new AuditService(db));
            var a = await svc.CreateAsync(projectId,
                new CreateActionRequest(
                    Title: "Do thing", Description: null, Source: null,
                    Priority: Priority.Medium, AssigneeId: null,
                    DueDate: null),
                userId, ip: "203.0.113.9", ua: "ua-act");
            actionId = a.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "action.created"));
        Assert.Equal("ActionItem", audit.Entity);
        Assert.Equal(actionId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
    }

    [Fact]
    public async Task ActionsService_UpdateAsync_emits_action_updated_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid actionId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ActionsService(db, new AuditService(db));
            var a = await svc.CreateAsync(projectId,
                new CreateActionRequest(
                    Title: "Do thing", Description: null, Source: null,
                    Priority: Priority.Medium, AssigneeId: userId,
                    DueDate: null),
                userId, null, null);
            actionId = a.Id;
            await svc.UpdateAsync(actionId, projectId,
                new UpdateActionRequest(
                    Title: "Done thing", Description: null,
                    Priority: null, Status: ActionStatus.Closed,
                    AssigneeId: null, DueDate: null),
                userId, UserRole.ProjectManager,
                ip: "203.0.113.10", ua: "ua-upd");
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "action.updated"));
        Assert.Equal("ActionItem", audit.Entity);
        Assert.Equal(actionId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
    }
}
