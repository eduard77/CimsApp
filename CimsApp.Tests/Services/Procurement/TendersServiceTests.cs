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
/// Behavioural tests for <see cref="TendersService"/> (T-S6-04).
/// Covers Submit (with the must-be-Issued-package guard), Withdraw
/// (Submitted-only), validation rules, listing, audit-twin
/// emission, cross-tenant 404.
/// </summary>
public class TendersServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid issuedPkgId, Guid draftPkgId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var issuedPkgId = Guid.NewGuid();
        var draftPkgId  = Guid.NewGuid();
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

        // Seed everything in one synchronous SaveChanges. Skip the
        // service layer for the package set-up — we don't need
        // tender_package.* audit events polluting the audit-log
        // assertions in these tests.
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
        seed.TenderPackages.AddRange(
            new TenderPackage
            {
                Id = issuedPkgId, ProjectId = projectId,
                Number = "TP-0001", Name = "Issued pkg",
                EstimatedValue = 100_000m,
                State = TenderPackageState.Issued,
                IssuedById = userId, IssuedAt = DateTime.UtcNow,
                CreatedById = userId,
            },
            new TenderPackage
            {
                Id = draftPkgId, ProjectId = projectId,
                Number = "TP-0002", Name = "Draft pkg",
                EstimatedValue = 100_000m,
                State = TenderPackageState.Draft,
                CreatedById = userId,
            });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, issuedPkgId, draftPkgId);
    }

    private static SubmitTenderRequest Basic(decimal bidAmount = 95_000m, string bidder = "Acme Civils Ltd") =>
        new(BidderName: bidder, BidderOrganisation: bidder,
            ContactEmail: "tenders@example.com", BidAmount: bidAmount);

    [Fact]
    public async Task SubmitAsync_against_Issued_package_persists_with_Submitted_state()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            id = (await svc.SubmitAsync(projectId, issuedPkgId, Basic(), userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var t = await verify.Tenders.SingleAsync(x => x.Id == id);
        Assert.Equal(TenderState.Submitted, t.State);
        Assert.Equal("Acme Civils Ltd",     t.BidderName);
        Assert.Equal(95_000m,                t.BidAmount);
        Assert.Equal(issuedPkgId,            t.TenderPackageId);
        Assert.Null(t.GeneratedContractId);
    }

    [Fact]
    public async Task SubmitAsync_emits_tender_submitted_audit()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            await svc.SubmitAsync(projectId, issuedPkgId, Basic(), userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "tender.submitted");
        Assert.Equal("Tender", row.Entity);
        Assert.Contains("Acme Civils Ltd",       row.Detail!);
        Assert.Contains("\"bidAmount\":95000",   row.Detail);
        Assert.Contains("TP-0001",               row.Detail);
    }

    [Fact]
    public async Task SubmitAsync_against_Draft_package_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, _, draftPkgId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TendersService(db, new AuditService(db));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.SubmitAsync(projectId, draftPkgId, Basic(), userId));
    }

    [Fact]
    public async Task SubmitAsync_against_Closed_package_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var pkgSvc = new TenderPackagesService(db, new AuditService(db));
            await pkgSvc.CloseAsync(projectId, issuedPkgId, userId, UserRole.ProjectManager);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc = new TendersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.SubmitAsync(projectId, issuedPkgId, Basic(), userId));
    }

    [Fact]
    public async Task SubmitAsync_rejects_empty_bidder_name()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TendersService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.SubmitAsync(projectId, issuedPkgId, Basic() with { BidderName = "  " }, userId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public async Task SubmitAsync_rejects_zero_or_negative_bid_amount(decimal amount)
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TendersService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.SubmitAsync(projectId, issuedPkgId, Basic(amount), userId));
    }

    [Fact]
    public async Task SubmitAsync_unknown_package_404s()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TendersService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.SubmitAsync(projectId, Guid.NewGuid(), Basic(), userId));
    }

    [Fact]
    public async Task WithdrawAsync_from_Submitted_succeeds()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            id = (await svc.SubmitAsync(projectId, issuedPkgId, Basic(), userId)).Id;
            await svc.WithdrawAsync(projectId, id,
                new WithdrawTenderRequest("Bidder withdrew citing capacity constraints"),
                userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var t = await verify.Tenders.SingleAsync(x => x.Id == id);
        Assert.Equal(TenderState.Withdrawn, t.State);
        Assert.Contains("capacity",         t.StateNote);
    }

    [Fact]
    public async Task WithdrawAsync_rejects_empty_note()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            id = (await svc.SubmitAsync(projectId, issuedPkgId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TendersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.WithdrawAsync(projectId, id, new WithdrawTenderRequest(""), userId));
    }

    [Fact]
    public async Task WithdrawAsync_already_Withdrawn_rejected()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            id = (await svc.SubmitAsync(projectId, issuedPkgId, Basic(), userId)).Id;
            await svc.WithdrawAsync(projectId, id,
                new WithdrawTenderRequest("First withdrawal"), userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TendersService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.WithdrawAsync(projectId, id,
                new WithdrawTenderRequest("Second withdrawal"), userId));
    }

    [Fact]
    public async Task ListAsync_returns_tenders_ordered_by_bid_amount()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            await svc.SubmitAsync(projectId, issuedPkgId, Basic(120_000m, "High bidder"),  userId);
            await svc.SubmitAsync(projectId, issuedPkgId, Basic(95_000m,  "Mid bidder"),   userId);
            await svc.SubmitAsync(projectId, issuedPkgId, Basic(85_000m,  "Low bidder"),   userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TendersService(db2, new AuditService(db2));
        var list = await svc2.ListAsync(projectId, issuedPkgId);
        Assert.Equal(3, list.Count);
        Assert.Equal("Low bidder",  list[0].BidderName);
        Assert.Equal("Mid bidder",  list[1].BidderName);
        Assert.Equal("High bidder", list[2].BidderName);
    }

    [Fact]
    public async Task ListAsync_unknown_package_404s()
    {
        var (options, tenant, _, _, projectId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TendersService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ListAsync(projectId, Guid.NewGuid()));
    }

    [Fact]
    public async Task SubmitAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId, issuedPkgId, _) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new TendersService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.SubmitAsync(projectId, issuedPkgId, Basic(), attacker.UserId!.Value));
    }
}
