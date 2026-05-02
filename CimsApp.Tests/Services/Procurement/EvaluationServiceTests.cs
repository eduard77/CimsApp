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
/// Behavioural tests for <see cref="EvaluationService"/> (T-S6-05).
/// Covers criteria CRUD with the Draft-only guard, score upsert
/// with the Issued-package + active-tender guards, matrix
/// computation through the Core function, and cross-tenant 404.
/// </summary>
public class EvaluationServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid draftPkgId, Guid issuedPkgId,
        Guid issuedTender1Id, Guid issuedTender2Id) BuildFixture()
    {
        var orgId           = Guid.NewGuid();
        var userId          = Guid.NewGuid();
        var projectId       = Guid.NewGuid();
        var draftPkgId      = Guid.NewGuid();
        var issuedPkgId     = Guid.NewGuid();
        var issuedTender1Id = Guid.NewGuid();
        var issuedTender2Id = Guid.NewGuid();

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
        seed.TenderPackages.AddRange(
            new TenderPackage
            {
                Id = draftPkgId, ProjectId = projectId,
                Number = "TP-0001", Name = "Draft pkg",
                EstimatedValue = 100_000m,
                State = TenderPackageState.Draft,
                CreatedById = userId,
            },
            new TenderPackage
            {
                Id = issuedPkgId, ProjectId = projectId,
                Number = "TP-0002", Name = "Issued pkg",
                EstimatedValue = 200_000m,
                State = TenderPackageState.Issued,
                IssuedById = userId, IssuedAt = DateTime.UtcNow,
                CreatedById = userId,
            });
        seed.Tenders.AddRange(
            new Tender
            {
                Id = issuedTender1Id, ProjectId = projectId,
                TenderPackageId = issuedPkgId,
                BidderName = "Acme Civils", BidAmount = 95_000m,
                State = TenderState.Submitted, CreatedById = userId,
            },
            new Tender
            {
                Id = issuedTender2Id, ProjectId = projectId,
                TenderPackageId = issuedPkgId,
                BidderName = "BetaBuild", BidAmount = 110_000m,
                State = TenderState.Submitted, CreatedById = userId,
            });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, draftPkgId, issuedPkgId,
                issuedTender1Id, issuedTender2Id);
    }

    // ── Criteria CRUD ───────────────────────────────────────────────

    [Fact]
    public async Task AddCriterionAsync_in_Draft_persists()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            id = (await svc.AddCriterionAsync(projectId, draftPkgId,
                new AddEvaluationCriterionRequest("Price", EvaluationCriterionType.Price, 0.6m),
                userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.EvaluationCriteria.SingleAsync(x => x.Id == id);
        Assert.Equal("Price",                          c.Name);
        Assert.Equal(EvaluationCriterionType.Price,    c.Type);
        Assert.Equal(0.6m,                              c.Weight);
    }

    [Fact]
    public async Task AddCriterionAsync_against_Issued_package_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId, _, issuedPkgId, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new EvaluationService(db, new AuditService(db));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.AddCriterionAsync(projectId, issuedPkgId,
                new AddEvaluationCriterionRequest("Price", EvaluationCriterionType.Price, 1m),
                userId));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public async Task AddCriterionAsync_rejects_weight_outside_zero_to_one(decimal weight)
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new EvaluationService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddCriterionAsync(projectId, draftPkgId,
                new AddEvaluationCriterionRequest("X", EvaluationCriterionType.Price, weight),
                userId));
    }

    [Fact]
    public async Task UpdateCriterionAsync_partial_update_in_Draft_succeeds()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            id = (await svc.AddCriterionAsync(projectId, draftPkgId,
                new AddEvaluationCriterionRequest("Price", EvaluationCriterionType.Price, 0.5m),
                userId)).Id;
            await svc.UpdateCriterionAsync(projectId, draftPkgId, id,
                new UpdateEvaluationCriterionRequest(null, null, Weight: 0.7m),
                userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.EvaluationCriteria.SingleAsync(x => x.Id == id);
        Assert.Equal(0.7m, c.Weight);
        Assert.Equal("Price", c.Name);
    }

    [Fact]
    public async Task RemoveCriterionAsync_in_Draft_deletes_row()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            id = (await svc.AddCriterionAsync(projectId, draftPkgId,
                new AddEvaluationCriterionRequest("Price", EvaluationCriterionType.Price, 0.6m),
                userId)).Id;
            await svc.RemoveCriterionAsync(projectId, draftPkgId, id, userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        Assert.False(await verify.EvaluationCriteria.AnyAsync(c => c.Id == id));
    }

    // ── Scores ──────────────────────────────────────────────────────

    private static async Task<(Guid priceId, Guid qualityId)> AddPriceAndQualityAsync(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid projectId, Guid pkgId, Guid userId)
    {
        // Add criteria to a Draft package, then transition the package
        // to Issued so scoring is allowed.
        Guid priceId, qualityId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            priceId   = (await svc.AddCriterionAsync(projectId, pkgId,
                new AddEvaluationCriterionRequest("Price",   EvaluationCriterionType.Price,   0.6m),
                userId)).Id;
            qualityId = (await svc.AddCriterionAsync(projectId, pkgId,
                new AddEvaluationCriterionRequest("Quality", EvaluationCriterionType.Quality, 0.4m),
                userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var pkg = await db.TenderPackages.SingleAsync(p => p.Id == pkgId);
            pkg.State = TenderPackageState.Issued;
            pkg.IssuedById = userId;
            pkg.IssuedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return (priceId, qualityId);
    }

    [Fact]
    public async Task SetScoreAsync_creates_then_updates_with_audit_each_time()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        var (priceId, _) = await AddPriceAndQualityAsync(options, tenant, projectId, draftPkgId, userId);

        // Submit a tender against the now-Issued package.
        Guid tenderId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            tenderId = (await svc.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Acme", null, null, 90_000m), userId)).Id;
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            await svc.SetScoreAsync(projectId, tenderId, priceId,
                new SetEvaluationScoreRequest(85m, "Initial"), userId);
            await svc.SetScoreAsync(projectId, tenderId, priceId,
                new SetEvaluationScoreRequest(88m, "Refined after clarification"), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var s = await verify.EvaluationScores
            .SingleAsync(x => x.TenderId == tenderId && x.CriterionId == priceId);
        Assert.Equal(88m, s.Score);
        Assert.Equal("Refined after clarification", s.Notes);
        // Two audit rows — one create, one update.
        var auditRows = await verify.AuditLogs.IgnoreQueryFilters()
            .CountAsync(a => a.Action == "evaluation_score.set");
        Assert.Equal(2, auditRows);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task SetScoreAsync_rejects_score_outside_zero_to_hundred(decimal score)
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        var (priceId, _) = await AddPriceAndQualityAsync(options, tenant, projectId, draftPkgId, userId);
        Guid tenderId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            tenderId = (await svc.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Acme", null, null, 90_000m), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EvaluationService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.SetScoreAsync(projectId, tenderId, priceId,
                new SetEvaluationScoreRequest(score, null), userId));
    }

    [Fact]
    public async Task SetScoreAsync_rejects_when_tender_is_Withdrawn()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        var (priceId, _) = await AddPriceAndQualityAsync(options, tenant, projectId, draftPkgId, userId);
        Guid tenderId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            tenderId = (await svc.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Acme", null, null, 90_000m), userId)).Id;
            await svc.WithdrawAsync(projectId, tenderId,
                new WithdrawTenderRequest("Pulled out"), userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EvaluationService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.SetScoreAsync(projectId, tenderId, priceId,
                new SetEvaluationScoreRequest(85m, null), userId));
    }

    // ── Matrix ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMatrixAsync_returns_per_tender_weighted_overall_scores()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        var (priceId, qualityId) = await AddPriceAndQualityAsync(options, tenant, projectId, draftPkgId, userId);

        // Two tenders, both fully scored.
        Guid t1, t2;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            t1 = (await svc.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Acme", null, null, 95_000m), userId)).Id;
            t2 = (await svc.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Beta", null, null, 110_000m), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            await svc.SetScoreAsync(projectId, t1, priceId,   new SetEvaluationScoreRequest(90m, null), userId);
            await svc.SetScoreAsync(projectId, t1, qualityId, new SetEvaluationScoreRequest(70m, null), userId);
            await svc.SetScoreAsync(projectId, t2, priceId,   new SetEvaluationScoreRequest(80m, null), userId);
            await svc.SetScoreAsync(projectId, t2, qualityId, new SetEvaluationScoreRequest(90m, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EvaluationService(db2, new AuditService(db2));
        var dto = await svc2.GetMatrixAsync(projectId, draftPkgId);

        Assert.Equal(1.0m, dto.TotalWeight);
        Assert.True(dto.IsValid);
        Assert.Equal(2, dto.Tenders.Count);

        // Acme: 0.6*90 + 0.4*70 = 54 + 28 = 82
        var acme = dto.Tenders.Single(r => r.BidderName == "Acme");
        Assert.Equal(82m, acme.OverallScore);

        // Beta: 0.6*80 + 0.4*90 = 48 + 36 = 84
        var beta = dto.Tenders.Single(r => r.BidderName == "Beta");
        Assert.Equal(84m, beta.OverallScore);
    }

    [Fact]
    public async Task GetMatrixAsync_OverallScore_null_when_partially_scored()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        var (priceId, _) = await AddPriceAndQualityAsync(options, tenant, projectId, draftPkgId, userId);
        Guid tenderId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new TendersService(db, new AuditService(db));
            tenderId = (await svc.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Acme", null, null, 95_000m), userId)).Id;
            // Only score Price, not Quality.
            var es = new EvaluationService(db, new AuditService(db));
            await es.SetScoreAsync(projectId, tenderId, priceId,
                new SetEvaluationScoreRequest(85m, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EvaluationService(db2, new AuditService(db2));
        var dto = await svc2.GetMatrixAsync(projectId, draftPkgId);
        Assert.Single(dto.Tenders);
        Assert.Null(dto.Tenders[0].OverallScore);
    }

    [Fact]
    public async Task GetMatrixAsync_excludes_Withdrawn_tenders()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        var (priceId, qualityId) = await AddPriceAndQualityAsync(options, tenant, projectId, draftPkgId, userId);
        Guid keepId, withdrawnId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var ts = new TendersService(db, new AuditService(db));
            keepId      = (await ts.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Keep", null, null, 90_000m), userId)).Id;
            withdrawnId = (await ts.SubmitAsync(projectId, draftPkgId,
                new SubmitTenderRequest("Drop", null, null, 95_000m), userId)).Id;
            await ts.WithdrawAsync(projectId, withdrawnId,
                new WithdrawTenderRequest("Out of capacity"), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EvaluationService(db2, new AuditService(db2));
        var dto = await svc2.GetMatrixAsync(projectId, draftPkgId);
        Assert.Single(dto.Tenders);
        Assert.Equal("Keep", dto.Tenders[0].BidderName);
    }

    [Fact]
    public async Task GetMatrixAsync_flags_invalid_when_weights_dont_sum_to_one()
    {
        var (options, tenant, _, userId, projectId, draftPkgId, _, _, _) = BuildFixture();
        // Add only one criterion at weight 0.6 — sum != 1.0.
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new EvaluationService(db, new AuditService(db));
            await svc.AddCriterionAsync(projectId, draftPkgId,
                new AddEvaluationCriterionRequest("Price", EvaluationCriterionType.Price, 0.6m),
                userId);
        }
        // Issue the package so we can submit / score (smoke path).
        using (var db = new CimsDbContext(options, tenant))
        {
            var pkg = await db.TenderPackages.SingleAsync(p => p.Id == draftPkgId);
            pkg.State = TenderPackageState.Issued;
            pkg.IssuedById = userId;
            pkg.IssuedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new EvaluationService(db2, new AuditService(db2));
        var dto = await svc2.GetMatrixAsync(projectId, draftPkgId);
        Assert.Equal(0.6m, dto.TotalWeight);
        Assert.False(dto.IsValid);
    }

    [Fact]
    public async Task GetMatrixAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, _, projectId, draftPkgId, _, _, _) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new EvaluationService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.GetMatrixAsync(projectId, draftPkgId));
    }
}
