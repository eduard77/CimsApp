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
/// Behavioural tests for <see cref="TenderPackagesService"/>
/// (T-S6-03). Covers Create with auto-numbering, Update only in
/// Draft state, Issue / Close transitions with role gates,
/// Deactivate only from Draft, listing + filter, audit-twin
/// emission, cross-tenant 404.
/// </summary>
public class TenderPackagesServiceTests
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

    private static CreateTenderPackageRequest Basic(string name = "Concrete frame package") =>
        new(Name: name, Description: "Substructure + superstructure RC frame",
            EstimatedValue: 750_000m,
            IssueDate: new DateTime(2026, 7, 1),
            ReturnDate: new DateTime(2026, 7, 22));

    [Fact]
    public async Task CreateAsync_persists_with_auto_number_and_Draft_state()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var t = await verify.TenderPackages.SingleAsync(x => x.Id == id);
        Assert.Equal("TP-0001",                   t.Number);
        Assert.Equal(TenderPackageState.Draft,    t.State);
        Assert.Equal(750_000m,                    t.EstimatedValue);
        Assert.Null(t.IssuedAt);
        Assert.True(t.IsActive);
    }

    [Fact]
    public async Task CreateAsync_auto_numbers_sequentially()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await svc.CreateAsync(projectId, Basic("First"),  userId);
        await svc.CreateAsync(projectId, Basic("Second"), userId);
        var third = await svc.CreateAsync(projectId, Basic("Third"), userId);
        Assert.Equal("TP-0003", third.Number);
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_name()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic() with { Name = "  " }, userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_negative_estimated_value()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic() with { EstimatedValue = -1m }, userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_return_date_not_after_issue_date()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                Basic() with { IssueDate = new DateTime(2026, 7, 1), ReturnDate = new DateTime(2026, 6, 1) },
                userId));
    }

    [Fact]
    public async Task UpdateAsync_partial_update_in_Draft_succeeds()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.UpdateAsync(projectId, id,
                new UpdateTenderPackageRequest(null, null,
                    EstimatedValue: 850_000m, null, null),
                userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var t = await verify.TenderPackages.SingleAsync(x => x.Id == id);
        Assert.Equal(850_000m, t.EstimatedValue);
        Assert.Equal("Concrete frame package", t.Name);
    }

    [Fact]
    public async Task UpdateAsync_after_Issue_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.IssueAsync(projectId, id, userId, UserRole.ProjectManager);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateTenderPackageRequest(Name: "Late update", null, null, null, null),
                userId));
    }

    [Fact]
    public async Task UpdateAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateTenderPackageRequest(null, null, null, null, null),
                userId));
    }

    [Fact]
    public async Task IssueAsync_records_issuer_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.IssueAsync(projectId, id, userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var t = await verify.TenderPackages.SingleAsync(x => x.Id == id);
        Assert.Equal(TenderPackageState.Issued, t.State);
        Assert.Equal(userId,                    t.IssuedById);
        Assert.NotNull(t.IssuedAt);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "tender_package.issued");
        Assert.Equal("TenderPackage", row.Entity);
    }

    [Fact]
    public async Task IssueAsync_rejects_TaskTeamMember_role()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc2.IssueAsync(projectId, id, userId, UserRole.TaskTeamMember));
    }

    [Fact]
    public async Task CloseAsync_from_Issued_succeeds()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.IssueAsync(projectId, id, userId, UserRole.ProjectManager);
            await svc.CloseAsync(projectId, id, userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var t = await verify.TenderPackages.SingleAsync(x => x.Id == id);
        Assert.Equal(TenderPackageState.Closed, t.State);
        Assert.NotNull(t.ClosedAt);
    }

    [Fact]
    public async Task CloseAsync_skipping_Issue_rejected_with_conflict()
    {
        // Draft → Closed direct: not allowed.
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.CloseAsync(projectId, id, userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task DeactivateAsync_only_allowed_in_Draft()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid draftId, issuedId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            draftId  = (await svc.CreateAsync(projectId, Basic("Draft pkg"), userId)).Id;
            issuedId = (await svc.CreateAsync(projectId, Basic("Issued pkg"), userId)).Id;
            await svc.IssueAsync(projectId, issuedId, userId, UserRole.ProjectManager);
            await svc.DeactivateAsync(projectId, draftId, userId);
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.DeactivateAsync(projectId, issuedId, userId));
        }
        using var verify = new CimsDbContext(options, tenant);
        Assert.False((await verify.TenderPackages.SingleAsync(t => t.Id == draftId)).IsActive);
        Assert.True((await verify.TenderPackages.SingleAsync(t => t.Id == issuedId)).IsActive);
    }

    [Fact]
    public async Task DeactivateAsync_idempotent_rejection_on_already_deactivated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.DeactivateAsync(projectId, id, userId));
    }

    [Fact]
    public async Task ListAsync_filters_by_state_and_excludes_deactivated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            var dropId = (await svc.CreateAsync(projectId, Basic("Drop"),  userId)).Id;
            await svc.CreateAsync(projectId, Basic("Keep1"), userId);
            await svc.CreateAsync(projectId, Basic("Keep2"), userId);
            await svc.DeactivateAsync(projectId, dropId, userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        var allDrafts = await svc2.ListAsync(projectId, TenderPackageState.Draft);
        Assert.Equal(2, allDrafts.Count);
        var allIssued = await svc2.ListAsync(projectId, TenderPackageState.Issued);
        Assert.Empty(allIssued);
        var unfiltered = await svc2.ListAsync(projectId, null);
        Assert.Equal(2, unfiltered.Count);
    }

    [Fact]
    public async Task IssueAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(), userId)).Id;
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.IssueAsync(projectId, id, attacker.UserId!.Value, UserRole.ProjectManager));
    }
}
