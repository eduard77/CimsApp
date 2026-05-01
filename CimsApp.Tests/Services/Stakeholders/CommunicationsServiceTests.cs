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
/// Behavioural tests for <see cref="CommunicationsService"/> (T-S3-07,
/// PAFM-SD F.4 fourth bullet — communications matrix). Covers Create
/// / Update / Deactivate / List, audit-twin emission, validation,
/// owner-membership enforcement, and the cross-tenant query-filter
/// 404.
/// </summary>
public class CommunicationsServiceTests
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
        // Owner-must-be-project-member rule needs a ProjectMember row.
        seed.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = projectId, UserId = userId, Role = UserRole.ProjectManager,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static CreateCommunicationItemRequest Basic(Guid ownerId,
        string itemType = "Monthly Project Report",
        CommunicationFrequency freq = CommunicationFrequency.Monthly,
        CommunicationChannel chan = CommunicationChannel.Email) =>
        new(ItemType: itemType,
            Audience: "Client, PM, IM",
            Frequency: freq,
            Channel: chan,
            OwnerId: ownerId,
            Notes: "Issued by 5th of each month, distribution list per BEP appendix B");

    [Fact]
    public async Task CreateAsync_persists_with_active_flag_and_normalised_strings()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId,
                Basic(userId) with { ItemType = "  Monthly Project Report  " },
                userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var item = await verify.CommunicationItems.SingleAsync(c => c.Id == id);
        Assert.True(item.IsActive);
        Assert.Equal("Monthly Project Report", item.ItemType);   // trimmed
        Assert.Equal(CommunicationFrequency.Monthly, item.Frequency);
        Assert.Equal(CommunicationChannel.Email, item.Channel);
        Assert.Equal(userId, item.OwnerId);
    }

    [Fact]
    public async Task CreateAsync_emits_communication_created_audit_twin()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            await svc.CreateAsync(projectId, Basic(userId), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "communication.created");
        Assert.Equal("CommunicationItem", row.Entity);
        Assert.Contains("Monthly Project Report", row.Detail!);
        Assert.Contains("Monthly", row.Detail);
        Assert.Contains("Email",   row.Detail);
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_item_type()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new CommunicationsService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic(userId) with { ItemType = "  " }, userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_audience()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new CommunicationsService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic(userId) with { Audience = "" }, userId));
    }

    [Fact]
    public async Task CreateAsync_rejects_owner_who_is_not_project_member()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        // Add a second user *without* a ProjectMember row.
        var outsiderId = Guid.NewGuid();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Users.Add(new User
            {
                Id = outsiderId, Email = $"o-{Guid.NewGuid():N}@example.com",
                PasswordHash = "x", FirstName = "O", LastName = "U",
                OrganisationId = tenant.OrganisationId!.Value,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new CommunicationsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId, Basic(outsiderId), userId));
    }

    [Fact]
    public async Task UpdateAsync_partial_update_changes_only_named_fields()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            await svc.UpdateAsync(projectId, id,
                new UpdateCommunicationItemRequest(
                    ItemType: null, Audience: null,
                    Frequency: CommunicationFrequency.Weekly,
                    Channel: null, OwnerId: null, Notes: null),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var item = await verify.CommunicationItems.SingleAsync(c => c.Id == id);
        Assert.Equal(CommunicationFrequency.Weekly, item.Frequency);
        Assert.Equal("Monthly Project Report", item.ItemType);   // unchanged
        Assert.Equal(CommunicationChannel.Email, item.Channel);  // unchanged
    }

    [Fact]
    public async Task UpdateAsync_owner_change_revalidates_project_membership()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
        }

        // Outsider — no ProjectMember row.
        var outsiderId = Guid.NewGuid();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Users.Add(new User
            {
                Id = outsiderId, Email = $"o-{Guid.NewGuid():N}@example.com",
                PasswordHash = "x", FirstName = "O", LastName = "U",
                OrganisationId = tenant.OrganisationId!.Value,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CommunicationsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateCommunicationItemRequest(null, null, null, null,
                    OwnerId: outsiderId, Notes: null),
                userId));
    }

    [Fact]
    public async Task UpdateAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CommunicationsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateCommunicationItemRequest(null, null, null, null, null, null),
                userId));
    }

    [Fact]
    public async Task UpdateAsync_rejects_already_deactivated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CommunicationsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateCommunicationItemRequest(ItemType: "X",
                    null, null, null, null, null),
                userId));
    }

    [Fact]
    public async Task DeactivateAsync_sets_IsActive_false_and_audits()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var item = await verify.CommunicationItems.SingleAsync(c => c.Id == id);
        Assert.False(item.IsActive);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "communication.deactivated");
        Assert.Contains("Monthly Project Report", row.Detail!);
    }

    [Fact]
    public async Task DeactivateAsync_rejects_already_deactivated()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
            await svc.DeactivateAsync(projectId, id, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CommunicationsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.DeactivateAsync(projectId, id, userId));
    }

    [Fact]
    public async Task ListAsync_returns_active_rows_ordered_by_ItemType_then_Frequency()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            await svc.CreateAsync(projectId,
                Basic(userId) with { ItemType = "Daily Diary",      Frequency = CommunicationFrequency.Daily },
                userId);
            await svc.CreateAsync(projectId,
                Basic(userId) with { ItemType = "Variation Notice", Frequency = CommunicationFrequency.AdHoc },
                userId);
            var staleId = (await svc.CreateAsync(projectId,
                Basic(userId) with { ItemType = "Stale Item", Frequency = CommunicationFrequency.Quarterly },
                userId)).Id;
            await svc.DeactivateAsync(projectId, staleId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CommunicationsService(db2, new AuditService(db2));
        var list = await svc2.ListAsync(projectId);

        Assert.Equal(2, list.Count);
        Assert.Equal("Daily Diary",      list[0].ItemType);
        Assert.Equal("Variation Notice", list[1].ItemType);
    }

    [Fact]
    public async Task UpdateAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CommunicationsService(db, new AuditService(db));
            id = (await svc.CreateAsync(projectId, Basic(userId), userId)).Id;
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new CommunicationsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.UpdateAsync(projectId, id,
                new UpdateCommunicationItemRequest(ItemType: "Pwn",
                    null, null, null, null, null),
                attacker.UserId!.Value));
    }
}
