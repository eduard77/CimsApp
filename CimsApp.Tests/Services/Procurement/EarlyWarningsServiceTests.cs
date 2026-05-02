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
/// Behavioural tests for <see cref="EarlyWarningsService"/>
/// (T-S6-07). Covers Raise (against Active contract only), the
/// linear Raised → UnderReview → Closed workflow, ResponseNote
/// requirement at Review, listing + filter, audit-twin emission,
/// cross-tenant 404.
/// </summary>
public class EarlyWarningsServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid activeContractId, Guid closedContractId)
        BuildFixture()
    {
        var orgId            = Guid.NewGuid();
        var userId           = Guid.NewGuid();
        var projectId        = Guid.NewGuid();
        var activeContractId = Guid.NewGuid();
        var closedContractId = Guid.NewGuid();
        var pkgId            = Guid.NewGuid();
        var tenderId         = Guid.NewGuid();

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
        seed.TenderPackages.Add(new TenderPackage
        {
            Id = pkgId, ProjectId = projectId,
            Number = "TP-0001", Name = "Pkg",
            EstimatedValue = 1_000_000m,
            State = TenderPackageState.Closed, AwardedTenderId = tenderId,
            CreatedById = userId,
        });
        seed.Tenders.Add(new Tender
        {
            Id = tenderId, ProjectId = projectId, TenderPackageId = pkgId,
            BidderName = "Acme", BidAmount = 950_000m,
            State = TenderState.Awarded, CreatedById = userId,
        });
        seed.Contracts.AddRange(
            new Contract
            {
                Id = activeContractId, ProjectId = projectId,
                Number = "CON-0001",
                TenderPackageId = pkgId, AwardedTenderId = tenderId,
                ContractorName = "Acme", ContractValue = 950_000m,
                ContractForm = ContractForm.Nec4OptionA,
                State = ContractState.Active,
                AwardedById = userId, AwardedAt = DateTime.UtcNow,
            },
            new Contract
            {
                Id = closedContractId, ProjectId = projectId,
                Number = "CON-0002",
                TenderPackageId = pkgId, AwardedTenderId = tenderId,
                ContractorName = "Beta", ContractValue = 500_000m,
                ContractForm = ContractForm.Nec4OptionA,
                State = ContractState.Closed,
                AwardedById = userId, AwardedAt = DateTime.UtcNow,
                ClosedAt = DateTime.UtcNow, ClosedById = userId,
            });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, activeContractId, closedContractId);
    }

    private static RaiseEarlyWarningRequest Basic(string title = "Concrete supply at risk") =>
        new(Title: title,
            Description: "Quarry strike likely to delay delivery from 2026-08-01");

    [Fact]
    public async Task RaiseAsync_against_Active_contract_persists_with_Raised_state()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, activeContractId, Basic(), userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var w = await verify.EarlyWarnings.SingleAsync(x => x.Id == id);
        Assert.Equal(EarlyWarningState.Raised, w.State);
        Assert.Equal(userId, w.RaisedById);
        Assert.Null(w.ReviewedAt);
        Assert.Null(w.ResponseNote);
    }

    [Fact]
    public async Task RaiseAsync_against_Closed_contract_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, _, closedContractId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new EarlyWarningsService(db, new AuditService(db));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.RaiseAsync(projectId, closedContractId, Basic(), userId));
    }

    [Fact]
    public async Task RaiseAsync_emits_audit_with_contract_number()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            await svc.RaiseAsync(projectId, activeContractId, Basic(), userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "early_warning.raised");
        Assert.Contains("CON-0001",                      row.Detail!);
        Assert.Contains("Concrete supply at risk",       row.Detail);
    }

    [Fact]
    public async Task RaiseAsync_rejects_empty_title()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new EarlyWarningsService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RaiseAsync(projectId, activeContractId, Basic("  "), userId));
    }

    [Fact]
    public async Task ReviewAsync_transitions_Raised_to_UnderReview_and_records_response()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, activeContractId, Basic(), userId)).Id;
            await svc.ReviewAsync(projectId, id,
                new ReviewEarlyWarningRequest("Sourcing alternative supplier; 2-week buffer"),
                userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var w = await verify.EarlyWarnings.SingleAsync(x => x.Id == id);
        Assert.Equal(EarlyWarningState.UnderReview, w.State);
        Assert.Equal(userId,                         w.ReviewedById);
        Assert.Contains("alternative supplier",      w.ResponseNote);
    }

    [Fact]
    public async Task ReviewAsync_rejects_empty_response_note()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, activeContractId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EarlyWarningsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.ReviewAsync(projectId, id, new ReviewEarlyWarningRequest(""), userId));
    }

    [Fact]
    public async Task ReviewAsync_already_UnderReview_rejected()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, activeContractId, Basic(), userId)).Id;
            await svc.ReviewAsync(projectId, id,
                new ReviewEarlyWarningRequest("First review"), userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EarlyWarningsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.ReviewAsync(projectId, id,
                new ReviewEarlyWarningRequest("Second review"), userId));
    }

    [Fact]
    public async Task CloseAsync_completes_workflow_with_optional_note()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, activeContractId, Basic(), userId)).Id;
            await svc.ReviewAsync(projectId, id,
                new ReviewEarlyWarningRequest("Mitigated"), userId);
            await svc.CloseAsync(projectId, id,
                new CloseEarlyWarningRequest("Risk no longer applies"), userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var w = await verify.EarlyWarnings.SingleAsync(x => x.Id == id);
        Assert.Equal(EarlyWarningState.Closed,   w.State);
        Assert.NotNull(w.ClosedAt);
        Assert.Equal("Risk no longer applies",   w.ClosureNote);

        // All three audit events present.
        var actions = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action.StartsWith("early_warning.")).Select(a => a.Action)
            .ToListAsync();
        Assert.Contains("early_warning.raised",    actions);
        Assert.Contains("early_warning.reviewed",  actions);
        Assert.Contains("early_warning.closed",    actions);
    }

    [Fact]
    public async Task CloseAsync_skipping_Review_rejected()
    {
        // Raised → Closed direct: not allowed; must Review first.
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, activeContractId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EarlyWarningsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.CloseAsync(projectId, id,
                new CloseEarlyWarningRequest(null), userId));
    }

    [Fact]
    public async Task ListAsync_filters_by_state()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid raisedId, reviewedId, closedId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EarlyWarningsService(db, new AuditService(db));
            raisedId   = (await svc.RaiseAsync(projectId, activeContractId, Basic("A"), userId)).Id;
            reviewedId = (await svc.RaiseAsync(projectId, activeContractId, Basic("B"), userId)).Id;
            closedId   = (await svc.RaiseAsync(projectId, activeContractId, Basic("C"), userId)).Id;
            await svc.ReviewAsync(projectId, reviewedId,
                new ReviewEarlyWarningRequest("Reviewed"), userId);
            await svc.ReviewAsync(projectId, closedId,
                new ReviewEarlyWarningRequest("Reviewed"), userId);
            await svc.CloseAsync(projectId, closedId,
                new CloseEarlyWarningRequest(null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EarlyWarningsService(db2, new AuditService(db2));
        var raised = await svc2.ListAsync(projectId, activeContractId, EarlyWarningState.Raised);
        Assert.Single(raised);
        Assert.Equal(raisedId, raised[0].Id);
        var allOnContract = await svc2.ListAsync(projectId, activeContractId, null);
        Assert.Equal(3, allOnContract.Count);
    }

    [Fact]
    public async Task RaiseAsync_unknown_contract_404s()
    {
        var (options, tenant, _, userId, projectId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new EarlyWarningsService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RaiseAsync(projectId, Guid.NewGuid(), Basic(), userId));
    }

    [Fact]
    public async Task RaiseAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, _, projectId, activeContractId, _) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new EarlyWarningsService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RaiseAsync(projectId, activeContractId, Basic(), attacker.UserId!.Value));
    }
}
