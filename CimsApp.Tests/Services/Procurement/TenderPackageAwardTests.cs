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
/// Behavioural tests for the AwardAsync slice of
/// <see cref="TenderPackagesService"/> (T-S6-06). Covers the
/// atomic Award→Contract spawn, auto-rejection of losing tenders,
/// package close-out, ContractForm default-from-strategy logic,
/// audit-twin events, and cross-tenant 404.
/// </summary>
public class TenderPackageAwardTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid pkgId,
        Guid winningTenderId, Guid loser1Id, Guid loser2Id) BuildFixture(
            ContractForm? strategyContractForm = null)
    {
        var orgId           = Guid.NewGuid();
        var userId          = Guid.NewGuid();
        var projectId       = Guid.NewGuid();
        var pkgId           = Guid.NewGuid();
        var winnerId        = Guid.NewGuid();
        var loser1Id        = Guid.NewGuid();
        var loser2Id        = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId, GlobalRole = UserRole.OrgAdmin,
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
        if (strategyContractForm.HasValue)
        {
            seed.ProcurementStrategies.Add(new ProcurementStrategy
            {
                ProjectId    = projectId,
                Approach     = ProcurementApproach.Traditional,
                ContractForm = strategyContractForm.Value,
            });
        }
        seed.TenderPackages.Add(new TenderPackage
        {
            Id = pkgId, ProjectId = projectId,
            Number = "TP-0001", Name = "Concrete frame",
            EstimatedValue = 1_000_000m,
            State = TenderPackageState.Issued,
            IssuedById = userId, IssuedAt = DateTime.UtcNow,
            CreatedById = userId,
        });
        seed.Tenders.AddRange(
            new Tender
            {
                Id = winnerId, ProjectId = projectId, TenderPackageId = pkgId,
                BidderName = "Acme Civils", BidderOrganisation = "Acme Civils Ltd",
                BidAmount = 950_000m,
                State = TenderState.Submitted, CreatedById = userId,
            },
            new Tender
            {
                Id = loser1Id, ProjectId = projectId, TenderPackageId = pkgId,
                BidderName = "BetaBuild", BidAmount = 1_050_000m,
                State = TenderState.Submitted, CreatedById = userId,
            },
            new Tender
            {
                Id = loser2Id, ProjectId = projectId, TenderPackageId = pkgId,
                BidderName = "Gamma Group", BidAmount = 1_100_000m,
                State = TenderState.Submitted, CreatedById = userId,
            });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, pkgId, winnerId, loser1Id, loser2Id);
    }

    private static AwardTenderPackageRequest Basic(Guid winnerId,
        ContractForm? form = null,
        DateTime? start = null, DateTime? end = null) =>
        new(AwardedTenderId: winnerId,
            AwardNote: "Lowest priced compliant tender",
            ContractForm: form,
            ContractStartDate: start, ContractEndDate: end);

    [Fact]
    public async Task AwardAsync_transitions_winner_and_losers_correctly()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, l1Id, l2Id) = BuildFixture();
        Guid contractId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            contractId = (await svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var winner = await verify.Tenders.SingleAsync(t => t.Id == winnerId);
        var l1     = await verify.Tenders.SingleAsync(t => t.Id == l1Id);
        var l2     = await verify.Tenders.SingleAsync(t => t.Id == l2Id);
        Assert.Equal(TenderState.Awarded,   winner.State);
        Assert.Equal(contractId,            winner.GeneratedContractId);
        Assert.Equal(TenderState.Rejected,  l1.State);
        Assert.Equal(TenderState.Rejected,  l2.State);
        Assert.Contains("Acme Civils",      l1.StateNote);   // "Not awarded; package awarded to Acme Civils"
    }

    [Fact]
    public async Task AwardAsync_closes_TenderPackage_with_AwardedTenderId()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager);
        }

        using var verify = new CimsDbContext(options, tenant);
        var pkg = await verify.TenderPackages.SingleAsync(t => t.Id == pkgId);
        Assert.Equal(TenderPackageState.Closed, pkg.State);
        Assert.Equal(winnerId,                  pkg.AwardedTenderId);
        Assert.NotNull(pkg.ClosedAt);
    }

    [Fact]
    public async Task AwardAsync_spawns_Contract_with_carried_over_values()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        Guid contractId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            var c = await svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager);
            contractId = c.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var contract = await verify.Contracts.SingleAsync(c => c.Id == contractId);
        Assert.Equal("CON-0001",              contract.Number);
        Assert.Equal("Acme Civils",           contract.ContractorName);
        Assert.Equal("Acme Civils Ltd",       contract.ContractorOrganisation);
        Assert.Equal(950_000m,                contract.ContractValue);
        Assert.Equal(ContractState.Active,    contract.State);
        Assert.Equal(winnerId,                contract.AwardedTenderId);
        Assert.Equal(pkgId,                   contract.TenderPackageId);
        Assert.Equal("Lowest priced compliant tender", contract.AwardNote);
    }

    [Fact]
    public async Task AwardAsync_uses_explicit_ContractForm_override()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture(
            strategyContractForm: ContractForm.Nec4OptionA);
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await svc.AwardAsync(projectId, pkgId,
                Basic(winnerId, form: ContractForm.Nec4OptionC),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.Contracts.SingleAsync();
        Assert.Equal(ContractForm.Nec4OptionC, c.ContractForm);
    }

    [Fact]
    public async Task AwardAsync_falls_back_to_strategy_ContractForm_when_omitted()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture(
            strategyContractForm: ContractForm.JctStandardBuilding);
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await svc.AwardAsync(projectId, pkgId,
                Basic(winnerId, form: null),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.Contracts.SingleAsync();
        Assert.Equal(ContractForm.JctStandardBuilding, c.ContractForm);
    }

    [Fact]
    public async Task AwardAsync_falls_back_to_Other_when_no_strategy_and_no_override()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture(
            strategyContractForm: null);
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await svc.AwardAsync(projectId, pkgId, Basic(winnerId, form: null),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.Contracts.SingleAsync();
        Assert.Equal(ContractForm.Other, c.ContractForm);
    }

    [Fact]
    public async Task AwardAsync_emits_full_audit_chain()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager);
        }

        using var verify = new CimsDbContext(options, tenant);
        var awarded   = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "tender.awarded");
        var rejected  = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "tender.rejected");
        var closed    = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "tender_package.closed");
        var contractCreated = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "contract.created");
        Assert.Equal(1, awarded);
        Assert.Equal(2, rejected);   // two losers
        Assert.Equal(1, closed);
        Assert.Equal(1, contractCreated);
    }

    [Fact]
    public async Task AwardAsync_rejects_empty_award_note()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AwardAsync(projectId, pkgId,
                Basic(winnerId) with { AwardNote = "  " },
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task AwardAsync_rejects_winner_in_Withdrawn_state()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var ts = new TendersService(db, new AuditService(db));
            await ts.WithdrawAsync(projectId, winnerId,
                new WithdrawTenderRequest("Bidder pulled out"), userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task AwardAsync_rejects_TaskTeamMember_role()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.TaskTeamMember));
    }

    [Fact]
    public async Task AwardAsync_rejects_winner_from_different_package()
    {
        var (options, tenant, _, userId, projectId, pkgId, _, _, _) = BuildFixture();
        // Add a second package + tender; try to award pkg1 to pkg2's tender.
        var pkg2Id = Guid.NewGuid();
        var alienTenderId = Guid.NewGuid();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.TenderPackages.Add(new TenderPackage
            {
                Id = pkg2Id, ProjectId = projectId, Number = "TP-0002",
                Name = "Other pkg", EstimatedValue = 50_000m,
                State = TenderPackageState.Issued,
                IssuedById = userId, IssuedAt = DateTime.UtcNow,
                CreatedById = userId,
            });
            db.Tenders.Add(new Tender
            {
                Id = alienTenderId, ProjectId = projectId, TenderPackageId = pkg2Id,
                BidderName = "Alien", BidAmount = 50_000m,
                State = TenderState.Submitted, CreatedById = userId,
            });
            await db.SaveChangesAsync();
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AwardAsync(projectId, pkgId, Basic(alienTenderId),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task AwardAsync_unknown_winner_404s()
    {
        var (options, tenant, _, userId, projectId, pkgId, _, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AwardAsync(projectId, pkgId, Basic(Guid.NewGuid()),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task AwardAsync_against_already_Closed_package_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, pkgId, winnerId, _, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TenderPackagesService(db, new AuditService(db));
            await svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager);
        }
        // Try to award again — package is now Closed.
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new TenderPackagesService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.AwardAsync(projectId, pkgId, Basic(winnerId),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task AwardAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, _, projectId, pkgId, winnerId, _, _) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new TenderPackagesService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AwardAsync(projectId, pkgId, Basic(winnerId),
                attacker.UserId!.Value, UserRole.ProjectManager));
    }
}
