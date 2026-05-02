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
/// Behavioural tests for <see cref="CompensationEventsService"/>
/// (T-S6-08). Covers the 5-state workflow with both rejection
/// branches (Notified→Rejected per clause 61.4 + Quoted→Rejected
/// standard), Quote validation, full happy-path Notified→Quoted→
/// Accepted→Implemented, role gates, audit chain, cross-tenant 404.
/// </summary>
public class CompensationEventsServiceTests
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
                Id = activeContractId, ProjectId = projectId, Number = "CON-0001",
                TenderPackageId = pkgId, AwardedTenderId = tenderId,
                ContractorName = "Acme", ContractValue = 950_000m,
                ContractForm = ContractForm.Nec4OptionA,
                State = ContractState.Active,
                AwardedById = userId, AwardedAt = DateTime.UtcNow,
            },
            new Contract
            {
                Id = closedContractId, ProjectId = projectId, Number = "CON-0002",
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

    private static NotifyCompensationEventRequest Notify(string title = "Site found unforeseen ground conditions") =>
        new(Title: title,
            Description: "Hard rock encountered at -3m depth not on GI report");

    private static QuoteCompensationEventRequest Quote(decimal cost = 75_000m, int days = 14) =>
        new(EstimatedCostImpact: cost, EstimatedTimeImpactDays: days,
            QuotationNote: "Additional rock-breaker hire + 2 weeks programme delay");

    // ── Notify ──────────────────────────────────────────────────────

    [Fact]
    public async Task NotifyAsync_against_Active_contract_persists_with_CE_NNNN()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var ce = await verify.CompensationEvents.SingleAsync(c => c.Id == id);
        Assert.Equal("CE-0001",                          ce.Number);
        Assert.Equal(CompensationEventState.Notified,    ce.State);
        Assert.Equal(userId,                              ce.NotifiedById);
        Assert.Null(ce.EstimatedCostImpact);
        Assert.Null(ce.EstimatedTimeImpactDays);
    }

    [Fact]
    public async Task NotifyAsync_against_Closed_contract_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, _, closedContractId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new CompensationEventsService(db, new AuditService(db));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.NotifyAsync(projectId, closedContractId, Notify(), userId));
    }

    [Fact]
    public async Task NotifyAsync_auto_numbers_sequentially()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new CompensationEventsService(db, new AuditService(db));
        await svc.NotifyAsync(projectId, activeContractId, Notify("CE 1"), userId);
        await svc.NotifyAsync(projectId, activeContractId, Notify("CE 2"), userId);
        var third = await svc.NotifyAsync(projectId, activeContractId, Notify("CE 3"), userId);
        Assert.Equal("CE-0003", third.Number);
    }

    // ── Quote ───────────────────────────────────────────────────────

    [Fact]
    public async Task QuoteAsync_records_cost_time_impact_and_rationale()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.QuoteAsync(projectId, id, Quote(),
                userId, UserRole.TaskTeamMember);
        }
        using var verify = new CimsDbContext(options, tenant);
        var ce = await verify.CompensationEvents.SingleAsync(c => c.Id == id);
        Assert.Equal(CompensationEventState.Quoted, ce.State);
        Assert.Equal(75_000m,                        ce.EstimatedCostImpact);
        Assert.Equal(14,                             ce.EstimatedTimeImpactDays);
        Assert.NotNull(ce.QuotedAt);
        Assert.Contains("rock-breaker",              ce.QuotationNote);
    }

    [Fact]
    public async Task QuoteAsync_rejects_negative_cost_impact()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CompensationEventsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.QuoteAsync(projectId, id, Quote(cost: -1m),
                userId, UserRole.TaskTeamMember));
    }

    [Fact]
    public async Task QuoteAsync_rejects_empty_quotation_note()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CompensationEventsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.QuoteAsync(projectId, id, Quote() with { QuotationNote = "" },
                userId, UserRole.TaskTeamMember));
    }

    // ── Accept ──────────────────────────────────────────────────────

    [Fact]
    public async Task AcceptAsync_transitions_Quoted_to_Accepted()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.QuoteAsync(projectId, id, Quote(),
                userId, UserRole.TaskTeamMember);
            await svc.AcceptAsync(projectId, id,
                new DecideCompensationEventRequest("Quote within budget envelope"),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var ce = await verify.CompensationEvents.SingleAsync(c => c.Id == id);
        Assert.Equal(CompensationEventState.Accepted, ce.State);
        Assert.Equal(userId, ce.DecisionById);
        Assert.Contains("budget envelope", ce.DecisionNote);
    }

    [Fact]
    public async Task AcceptAsync_skipping_Quote_rejected_with_conflict()
    {
        // Notified → Accepted direct: not allowed.
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CompensationEventsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.AcceptAsync(projectId, id,
                new DecideCompensationEventRequest("Trying to skip"),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task AcceptAsync_rejects_TaskTeamMember_role()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.QuoteAsync(projectId, id, Quote(),
                userId, UserRole.TaskTeamMember);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CompensationEventsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc2.AcceptAsync(projectId, id,
                new DecideCompensationEventRequest("Trying TTM accept"),
                userId, UserRole.TaskTeamMember));
    }

    // ── Reject (both branches) ──────────────────────────────────────

    [Fact]
    public async Task RejectAsync_from_Notified_clause_61_4_path()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.RejectAsync(projectId, id,
                new DecideCompensationEventRequest("Clause 61.4 — not a CE per contract"),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var ce = await verify.CompensationEvents.SingleAsync(c => c.Id == id);
        Assert.Equal(CompensationEventState.Rejected, ce.State);
        Assert.Contains("61.4", ce.DecisionNote);
    }

    [Fact]
    public async Task RejectAsync_from_Quoted_succeeds()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.QuoteAsync(projectId, id, Quote(),
                userId, UserRole.TaskTeamMember);
            await svc.RejectAsync(projectId, id,
                new DecideCompensationEventRequest("Quote too high"),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var ce = await verify.CompensationEvents.SingleAsync(c => c.Id == id);
        Assert.Equal(CompensationEventState.Rejected, ce.State);
    }

    [Fact]
    public async Task RejectAsync_after_Accepted_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.QuoteAsync(projectId, id, Quote(),
                userId, UserRole.TaskTeamMember);
            await svc.AcceptAsync(projectId, id,
                new DecideCompensationEventRequest("Approved"),
                userId, UserRole.ProjectManager);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CompensationEventsService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.RejectAsync(projectId, id,
                new DecideCompensationEventRequest("Changed mind"),
                userId, UserRole.ProjectManager));
    }

    // ── Implement ───────────────────────────────────────────────────

    [Fact]
    public async Task ImplementAsync_completes_the_full_workflow()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            id = (await svc.NotifyAsync(projectId, activeContractId, Notify(), userId)).Id;
            await svc.QuoteAsync(projectId, id, Quote(),
                userId, UserRole.TaskTeamMember);
            await svc.AcceptAsync(projectId, id,
                new DecideCompensationEventRequest("Approved"),
                userId, UserRole.ProjectManager);
            await svc.ImplementAsync(projectId, id,
                new ImplementCompensationEventRequest("Done by site team week 22"),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var ce = await verify.CompensationEvents.SingleAsync(c => c.Id == id);
        Assert.Equal(CompensationEventState.Implemented, ce.State);
        Assert.NotNull(ce.ImplementedAt);
        Assert.Equal(userId, ce.ImplementedById);

        // All four expected audit events emitted across the lifecycle.
        var actions = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action.StartsWith("compensation_event."))
            .Select(a => a.Action).ToListAsync();
        Assert.Contains("compensation_event.notified",    actions);
        Assert.Contains("compensation_event.quoted",      actions);
        Assert.Contains("compensation_event.accepted",    actions);
        Assert.Contains("compensation_event.implemented", actions);
    }

    // ── Listing + filtering ─────────────────────────────────────────

    [Fact]
    public async Task ListAsync_filters_by_state()
    {
        var (options, tenant, _, userId, projectId, activeContractId, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CompensationEventsService(db, new AuditService(db));
            var n1 = (await svc.NotifyAsync(projectId, activeContractId, Notify("Notified-1"), userId)).Id;
            var n2 = (await svc.NotifyAsync(projectId, activeContractId, Notify("Notified-2"), userId)).Id;
            var q1 = (await svc.NotifyAsync(projectId, activeContractId, Notify("Quoted-1"),   userId)).Id;
            await svc.QuoteAsync(projectId, q1, Quote(), userId, UserRole.TaskTeamMember);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CompensationEventsService(db2, new AuditService(db2));
        var notified = await svc2.ListAsync(projectId, activeContractId, CompensationEventState.Notified);
        var quoted   = await svc2.ListAsync(projectId, activeContractId, CompensationEventState.Quoted);
        Assert.Equal(2, notified.Count);
        Assert.Single(quoted);
    }

    [Fact]
    public async Task NotifyAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, _, projectId, activeContractId, _) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new CompensationEventsService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.NotifyAsync(projectId, activeContractId, Notify(), attacker.UserId!.Value));
    }
}
