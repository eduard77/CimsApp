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

namespace CimsApp.Tests.Services.Iso19650;

/// <summary>
/// Behavioural tests for <see cref="MidpService"/> (T-S9-05). Covers
/// the per-project Master Information Delivery Plan CRUD + completion
/// path. v1.0 ships the simple delivery-list shape; full ISO 19650-2
/// §5 information-requirements model is deferred per the kickoff.
/// </summary>
public class MidpServiceTests
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

    private static (MidpService svc, CimsDbContext db) NewSvc(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        return (new MidpService(db, new AuditService(db)), db);
    }

    [Fact]
    public async Task CreateAsync_persists_entry_with_owner_and_due_date()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, db) = NewSvc(options, tenant);

        var dto = await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest(
                Title: "Coordinated structural model — Stage 4",
                Description: "Due before tender package issue",
                DocTypeFilter: "M3",
                DueDate: new DateTime(2026, 9, 30),
                OwnerId: userId),
            actorId: userId, ip: null, ua: null);

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal("M3", dto.DocTypeFilter);
        Assert.Equal(userId, dto.OwnerId);

        var stored = await db.MidpEntries.SingleAsync();
        Assert.True(stored.IsActive);
        Assert.False(stored.IsCompleted);
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_DocTypeFilter()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(projectId,
                new CreateMidpEntryRequest("X", null, "ZZ", DateTime.UtcNow, userId),
                userId, null, null));
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_owner()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateAsync(projectId,
                new CreateMidpEntryRequest("X", null, null, DateTime.UtcNow,
                    OwnerId: Guid.NewGuid()),
                userId, null, null));
    }

    [Fact]
    public async Task ListAsync_returns_active_rows_ordered_by_due_date()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Late", null, null, new DateTime(2026, 12, 1), userId),
            userId, null, null);
        await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Early", null, null, new DateTime(2026, 6, 1), userId),
            userId, null, null);
        await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Mid",  null, null, new DateTime(2026, 9, 1), userId),
            userId, null, null);

        var rows = await svc.ListAsync(projectId);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Early", rows[0].Title);
        Assert.Equal("Mid",   rows[1].Title);
        Assert.Equal("Late",  rows[2].Title);
    }

    [Fact]
    public async Task UpdateAsync_changes_only_supplied_fields_and_emits_changedFields()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        var created = await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Original", null, "RP",
                new DateTime(2026, 9, 30), userId),
            userId, null, null);

        var updated = await svc.UpdateAsync(projectId, created.Id,
            new UpdateMidpEntryRequest(
                Title: "Updated",
                Description: null,
                DocTypeFilter: null,
                DueDate: new DateTime(2026, 10, 15),
                OwnerId: null),
            userId, null, null);

        Assert.Equal("Updated", updated.Title);
        Assert.Equal(new DateTime(2026, 10, 15), updated.DueDate);
        Assert.Equal("RP", updated.DocTypeFilter);
    }

    [Fact]
    public async Task UpdateAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, _) = NewSvc(options, tenant);
        var created = await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("X", null, null, DateTime.UtcNow, userId),
            userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateAsync(projectId, created.Id,
                new UpdateMidpEntryRequest(null, null, null, null, null),
                userId, null, null));
    }

    [Fact]
    public async Task CompleteAsync_attaches_document_when_DocTypeFilter_matches()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid docId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var doc = new Document
            {
                ProjectId = projectId,
                ProjectCode = "PRJ", Originator = "ABC",
                DocType = "RP", Number = "0001",
                DocumentNumber = "PRJ-ABC-ZZ-ZZ-RP-XX-0001",
                Title = "Phase report", CreatorId = userId,
            };
            db.Documents.Add(doc);
            db.SaveChanges();
            docId = doc.Id;
        }
        var (svc, _) = NewSvc(options, tenant);
        var entry = await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Phase 1 report", null, "RP",
                DateTime.UtcNow, userId),
            userId, null, null);

        var completed = await svc.CompleteAsync(projectId, entry.Id,
            new CompleteMidpEntryRequest(docId), userId, null, null);

        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(docId, completed.DocumentId);
    }

    [Fact]
    public async Task CompleteAsync_rejects_document_with_mismatching_DocType()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid docId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var doc = new Document
            {
                ProjectId = projectId,
                ProjectCode = "PRJ", Originator = "ABC",
                DocType = "DR", Number = "0001",
                DocumentNumber = "PRJ-ABC-ZZ-ZZ-DR-XX-0001",
                Title = "Wrong type", CreatorId = userId,
            };
            db.Documents.Add(doc);
            db.SaveChanges();
            docId = doc.Id;
        }
        var (svc, _) = NewSvc(options, tenant);
        var entry = await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Specification due", null, "SP",
                DateTime.UtcNow, userId),
            userId, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CompleteAsync(projectId, entry.Id,
                new CompleteMidpEntryRequest(docId), userId, null, null));
    }

    [Fact]
    public async Task DeleteAsync_soft_deletes_and_excludes_from_list()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var (svc, db) = NewSvc(options, tenant);
        var created = await svc.CreateAsync(projectId,
            new CreateMidpEntryRequest("Doomed", null, null, DateTime.UtcNow, userId),
            userId, null, null);

        await svc.DeleteAsync(projectId, created.Id, userId, null, null);

        var listed = await svc.ListAsync(projectId);
        Assert.Empty(listed);
        var stored = await db.MidpEntries.SingleAsync();
        Assert.False(stored.IsActive);
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
        var (svc, _) = NewSvc(options, otherTenant);

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.GetAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
