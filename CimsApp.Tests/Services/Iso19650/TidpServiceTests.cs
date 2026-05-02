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
/// Behavioural tests for <see cref="TidpService"/> (T-S9-06). Covers
/// per-team task delivery plan rows that FK back to MidpEntry. Sign-
/// off is one-way in v1.0; un-sign-off would be a workflow concern
/// for v1.1 if pilot need surfaces.
/// </summary>
public class TidpServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid midpId) BuildFixtureWithMidp()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var midpId    = Guid.NewGuid();
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
        seed.MidpEntries.Add(new MidpEntry
        {
            Id = midpId, ProjectId = projectId,
            Title = "Parent MIDP", DueDate = new DateTime(2026, 9, 30),
            OwnerId = userId,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, midpId);
    }

    private static (TidpService svc, CimsDbContext db) NewSvc(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        return (new TidpService(db, new AuditService(db)), db);
    }

    [Fact]
    public async Task CreateAsync_persists_team_entry_against_parent_midp()
    {
        var (options, tenant, _, userId, projectId, midpId) = BuildFixtureWithMidp();
        var (svc, _) = NewSvc(options, tenant);

        var dto = await svc.CreateAsync(projectId,
            new CreateTidpEntryRequest(midpId, "Architecture", new DateTime(2026, 9, 15)),
            userId, null, null);

        Assert.Equal(midpId, dto.MidpEntryId);
        Assert.Equal("Architecture", dto.TeamName);
        Assert.False(dto.IsSignedOff);
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_MidpEntry()
    {
        var (options, tenant, _, userId, projectId, _) = BuildFixtureWithMidp();
        var (svc, _) = NewSvc(options, tenant);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateAsync(projectId,
                new CreateTidpEntryRequest(Guid.NewGuid(), "Foo", DateTime.UtcNow),
                userId, null, null));
    }

    [Fact]
    public async Task ListAsync_filters_by_midpEntryId_when_supplied()
    {
        var (options, tenant, _, userId, projectId, midpId) = BuildFixtureWithMidp();
        var (svc, db) = NewSvc(options, tenant);

        // Add a second MidpEntry so list filtering is meaningful.
        var midp2 = Guid.NewGuid();
        db.MidpEntries.Add(new MidpEntry
        {
            Id = midp2, ProjectId = projectId,
            Title = "Other MIDP", DueDate = DateTime.UtcNow, OwnerId = userId,
        });
        await db.SaveChangesAsync();

        await svc.CreateAsync(projectId,
            new CreateTidpEntryRequest(midpId, "Architecture", DateTime.UtcNow),
            userId, null, null);
        await svc.CreateAsync(projectId,
            new CreateTidpEntryRequest(midp2, "MEP", DateTime.UtcNow),
            userId, null, null);

        var filtered = await svc.ListAsync(projectId, midpId);
        Assert.Single(filtered);
        Assert.Equal("Architecture", filtered[0].TeamName);

        var allRows = await svc.ListAsync(projectId);
        Assert.Equal(2, allRows.Count);
    }

    [Fact]
    public async Task SignOffAsync_records_actor_and_timestamp()
    {
        var (options, tenant, _, userId, projectId, midpId) = BuildFixtureWithMidp();
        var (svc, _) = NewSvc(options, tenant);
        var entry = await svc.CreateAsync(projectId,
            new CreateTidpEntryRequest(midpId, "Architecture", DateTime.UtcNow),
            userId, null, null);

        var signedOff = await svc.SignOffAsync(projectId, entry.Id,
            new SignOffTidpEntryRequest("All deliverables submitted"),
            userId, null, null);

        Assert.True(signedOff.IsSignedOff);
        Assert.Equal(userId, signedOff.SignedOffById);
        Assert.NotNull(signedOff.SignedOffAt);
        Assert.Equal("All deliverables submitted", signedOff.SignOffNote);
    }

    [Fact]
    public async Task SignOffAsync_rejects_already_signed_off_entry()
    {
        var (options, tenant, _, userId, projectId, midpId) = BuildFixtureWithMidp();
        var (svc, _) = NewSvc(options, tenant);
        var entry = await svc.CreateAsync(projectId,
            new CreateTidpEntryRequest(midpId, "Architecture", DateTime.UtcNow),
            userId, null, null);
        await svc.SignOffAsync(projectId, entry.Id,
            new SignOffTidpEntryRequest(null), userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.SignOffAsync(projectId, entry.Id,
                new SignOffTidpEntryRequest("retry"), userId, null, null));
    }

    [Fact]
    public async Task UpdateAsync_rejects_edits_to_signed_off_entry()
    {
        var (options, tenant, _, userId, projectId, midpId) = BuildFixtureWithMidp();
        var (svc, _) = NewSvc(options, tenant);
        var entry = await svc.CreateAsync(projectId,
            new CreateTidpEntryRequest(midpId, "Architecture", DateTime.UtcNow),
            userId, null, null);
        await svc.SignOffAsync(projectId, entry.Id,
            new SignOffTidpEntryRequest(null), userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateAsync(projectId, entry.Id,
                new UpdateTidpEntryRequest("MEP", null), userId, null, null));
    }

    [Fact]
    public async Task GetAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _, _) = BuildFixtureWithMidp();
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
