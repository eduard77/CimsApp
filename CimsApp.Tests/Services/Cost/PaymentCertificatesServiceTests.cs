using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Cost;

/// <summary>
/// Behavioural tests for <see cref="PaymentCertificatesService"/>
/// (T-S1-09). NEC4 cumulative semantics per ADR-0013: retention on
/// (Valuation + Variations) but NOT Materials; AmountDue is the
/// cumulative net minus the latest prior Issued cert's net.
/// </summary>
public class PaymentCertificatesServiceTests
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

    private static async Task<Guid> SeedPeriodAsync(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid projectId, string label, DateTime start, DateTime end, Guid userId)
    {
        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var p = await svc.CreatePeriodAsync(projectId,
            new CreatePeriodRequest(label, start, end), userId);
        return p.Id;
    }

    private static DateTime Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CreateDraft_writes_row_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr 2026", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        PaymentCertificateDto dto;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new PaymentCertificatesService(db, new AuditService(db));
            dto = await svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 100_000m, 5_000m, 3.00m),
                userId);
        }

        Assert.Equal("PC-0001", dto.CertificateNumber);
        Assert.Equal(PaymentCertificateState.Draft, dto.State);
        Assert.Equal(100_000m, dto.CumulativeValuation);
        Assert.Equal(5_000m, dto.CumulativeMaterialsOnSite);
        Assert.Equal(3.00m, dto.RetentionPercent);
        // No variations seeded → variations preview is 0.
        Assert.Equal(0m, dto.IncludedVariationsAmount);
        // Gross = 100_000 + 0 + 5_000 = 105_000.
        Assert.Equal(105_000m, dto.CumulativeGross);
        // Retention base excludes Materials per ADR-0013:
        // (100_000 + 0) × 3% = 3_000.
        Assert.Equal(3_000m, dto.RetentionAmount);
        Assert.Equal(102_000m, dto.CumulativeNet);     // 105_000 − 3_000
        Assert.Equal(0m, dto.PreviouslyCertified);
        Assert.Equal(102_000m, dto.AmountDue);
        Assert.Null(dto.IssuedAt);

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "payment_certificate.draft_created"));
        Assert.Contains("\"number\":\"PC-0001\"", audit.Detail);
        Assert.Contains("\"valuation\":100000", audit.Detail);
        Assert.Contains("\"retentionPercent\":3.00", audit.Detail);
    }

    [Fact]
    public async Task CreateDraft_validates_inputs()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));

        var negVal = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, -1m, 0m, 3m), userId));
        Assert.Contains("CumulativeValuation must be zero or greater", negVal.Errors);

        var negMat = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 0m, -1m, 3m), userId));
        Assert.Contains("CumulativeMaterialsOnSite must be zero or greater", negMat.Errors);

        var negRet = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 0m, 0m, -1m), userId));
        Assert.Contains("RetentionPercent must be between 0 and 100", negRet.Errors);

        var hugeRet = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 0m, 0m, 101m), userId));
        Assert.Contains("RetentionPercent must be between 0 and 100", hugeRet.Errors);
    }

    [Fact]
    public async Task CreateDraft_two_certs_on_same_period_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));
        await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(periodId, 100m, 0m, 3m), userId);
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 200m, 0m, 3m), userId));
    }

    [Fact]
    public async Task CreateDraft_period_in_wrong_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var projectB = Guid.NewGuid();
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            seed.SaveChanges();
        }
        var periodOnB = await SeedPeriodAsync(options, tenant, projectB,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.CreateDraftAsync(projectA,
                new CreatePaymentCertificateDraftRequest(periodOnB, 100m, 0m, 3m), userId));
    }

    [Fact]
    public async Task UpdateDraft_changes_inputs_and_recomputes()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        Guid certId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new PaymentCertificatesService(db, new AuditService(db));
            var d = await svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 100m, 0m, 3m), userId);
            certId = d.Id;
            var updated = await svc.UpdateDraftAsync(projectId, certId,
                new UpdatePaymentCertificateDraftRequest(500m, 50m, 5m), userId);
            Assert.Equal(500m, updated.CumulativeValuation);
            Assert.Equal(50m, updated.CumulativeMaterialsOnSite);
            Assert.Equal(5m, updated.RetentionPercent);
            // Gross = 500 + 0 + 50 = 550; Retention base = 500;
            // Retention = 25; Net = 525.
            Assert.Equal(550m, updated.CumulativeGross);
            Assert.Equal(25m, updated.RetentionAmount);
            Assert.Equal(525m, updated.CumulativeNet);
        }
    }

    [Fact]
    public async Task UpdateDraft_on_issued_cert_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));
        var d = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(periodId, 100m, 0m, 3m), userId);
        await svc.IssueAsync(projectId, d.Id, userId);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateDraftAsync(projectId, d.Id,
                new UpdatePaymentCertificateDraftRequest(200m, 0m, 3m), userId));
        Assert.Contains("Issued", ex.Message);
    }

    [Fact]
    public async Task Issue_snapshots_approved_variations_amount()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        // Approved variation totalling 12_345 in the project before issue.
        using (var db = new CimsDbContext(options, tenant))
        {
            var vsvc = new VariationsService(db, new AuditService(db));
            var v1 = await vsvc.RaiseAsync(projectId,
                new RaiseVariationRequest("V1", null, null, 12_345m, null, null), userId);
            await vsvc.ApproveAsync(projectId, v1.Id,
                new VariationDecisionRequest("ok"), userId);

            // A second variation, RAISED but not yet approved — must NOT
            // contribute to the snapshot.
            await vsvc.RaiseAsync(projectId,
                new RaiseVariationRequest("V2", null, null, 99_999m, null, null), userId);
        }

        PaymentCertificateDto dto;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new PaymentCertificatesService(db, new AuditService(db));
            var draft = await svc.CreateDraftAsync(projectId,
                new CreatePaymentCertificateDraftRequest(periodId, 100_000m, 0m, 3m), userId);
            // Live preview on the draft already reflects the approved
            // variation only (12_345, not 99_999 + 12_345).
            Assert.Equal(12_345m, draft.IncludedVariationsAmount);
            dto = await svc.IssueAsync(projectId, draft.Id, userId);
        }

        Assert.Equal(PaymentCertificateState.Issued, dto.State);
        Assert.Equal(12_345m, dto.IncludedVariationsAmount);
        // Gross = 100_000 + 12_345 + 0 = 112_345.
        Assert.Equal(112_345m, dto.CumulativeGross);
        // Retention base = 100_000 + 12_345 = 112_345; × 3% = 3_370.35.
        Assert.Equal(3_370.35m, dto.RetentionAmount);
        Assert.Equal(108_974.65m, dto.CumulativeNet);   // 112_345 − 3_370.35
        Assert.NotNull(dto.IssuedAt);
    }

    [Fact]
    public async Task Issue_after_issue_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));
        var d = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(periodId, 100m, 0m, 3m), userId);
        await svc.IssueAsync(projectId, d.Id, userId);
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.IssueAsync(projectId, d.Id, userId));
    }

    [Fact]
    public async Task Cumulative_chain_across_three_periods_computes_correct_AmountDue()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var apr = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);
        var may = await SeedPeriodAsync(options, tenant, projectId,
            "May", Utc(2026, 5, 1), Utc(2026, 5, 31), userId);
        var jun = await SeedPeriodAsync(options, tenant, projectId,
            "Jun", Utc(2026, 6, 1), Utc(2026, 6, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));

        // April: cumulative valuation 100_000, no materials, 3% retention.
        // Net = 100_000 × (1 − 0.03) = 97_000. AmountDue = 97_000.
        var c1 = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(apr, 100_000m, 0m, 3m), userId);
        c1 = await svc.IssueAsync(projectId, c1.Id, userId);
        Assert.Equal(97_000m, c1.CumulativeNet);
        Assert.Equal(0m, c1.PreviouslyCertified);
        Assert.Equal(97_000m, c1.AmountDue);

        // May: cumulative valuation 250_000. Net = 242_500.
        // PreviouslyCertified = 97_000 (April's net). AmountDue = 145_500.
        var c2 = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(may, 250_000m, 0m, 3m), userId);
        c2 = await svc.IssueAsync(projectId, c2.Id, userId);
        Assert.Equal(242_500m, c2.CumulativeNet);
        Assert.Equal(97_000m, c2.PreviouslyCertified);
        Assert.Equal(145_500m, c2.AmountDue);

        // June: cumulative valuation 400_000 + materials 10_000.
        // Gross = 410_000. Retention base = 400_000. Retention = 12_000.
        // Net = 398_000. PreviouslyCertified = 242_500. AmountDue = 155_500.
        var c3 = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(jun, 400_000m, 10_000m, 3m), userId);
        c3 = await svc.IssueAsync(projectId, c3.Id, userId);
        Assert.Equal(410_000m, c3.CumulativeGross);
        Assert.Equal(12_000m, c3.RetentionAmount);
        Assert.Equal(398_000m, c3.CumulativeNet);
        Assert.Equal(242_500m, c3.PreviouslyCertified);
        Assert.Equal(155_500m, c3.AmountDue);

        // Conservation: Σ AmountDue = latest cumulative net.
        Assert.Equal(c3.CumulativeNet, c1.AmountDue + c2.AmountDue + c3.AmountDue);
    }

    [Fact]
    public async Task Get_returns_dto_with_computed_fields()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));
        var draft = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(periodId, 1_000m, 100m, 5m), userId);

        var got = await svc.GetAsync(projectId, draft.Id);
        Assert.Equal(draft.Id, got.Id);
        Assert.Equal(1_100m, got.CumulativeGross);    // 1_000 + 0 + 100
        Assert.Equal(50m, got.RetentionAmount);        // 1_000 × 5% = 50
        Assert.Equal(1_050m, got.CumulativeNet);       // 1_100 − 50
    }

    [Fact]
    public async Task Get_dto_carries_DerivedValuationFromProgress_for_T_S1_09_auto_derive()
    {
        // T-S1-09 / B-017 valuation auto-derive (NEC4 PWDD per ADR-0013).
        // Three CBS lines with Budget × PercentComplete:
        //   Line A: 1000 × 0.50 = 500
        //   Line B: 2000 × 0.25 = 500
        //   Line C: 500  × null = 0   (no progress reported → contributes 0)
        // Σ = 1000.
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var periodId = await SeedPeriodAsync(options, tenant, projectId,
            "Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), userId);
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.CostBreakdownItems.AddRange(
                new CostBreakdownItem { ProjectId = projectId, Code = "A", Name = "A", Budget = 1000m, PercentComplete = 0.5m },
                new CostBreakdownItem { ProjectId = projectId, Code = "B", Name = "B", Budget = 2000m, PercentComplete = 0.25m },
                new CostBreakdownItem { ProjectId = projectId, Code = "C", Name = "C", Budget = 500m,  PercentComplete = null });
            seed.SaveChanges();
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new PaymentCertificatesService(db, new AuditService(db));
        // Manually-stated valuation 950 — DIFFERENT from the derived 1000 —
        // so we can prove the two fields are independent (stored remains
        // source of truth; derived is the progress-based guide).
        var dto = await svc.CreateDraftAsync(projectId,
            new CreatePaymentCertificateDraftRequest(periodId, 950m, 0m, 3m), userId);

        Assert.Equal(950m,  dto.CumulativeValuation);            // assessor-stated.
        Assert.Equal(1000m, dto.DerivedValuationFromProgress);   // Σ Budget × PC.
    }

    [Fact]
    public async Task Cross_tenant_certificate_lookup_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        // Seed a period + cert under B.
        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        Guid certIdOnB;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }
        Guid periodIdOnB;
        using (var dbB = new CimsDbContext(optionsB, tenantB))
        {
            var costSvc = new CostService(dbB, new AuditService(dbB));
            var p = await costSvc.CreatePeriodAsync(projectB,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30)),
                userB);
            periodIdOnB = p.Id;
        }
        using (var dbB = new CimsDbContext(optionsB, tenantB))
        {
            var svc = new PaymentCertificatesService(dbB, new AuditService(dbB));
            var d = await svc.CreateDraftAsync(projectB,
                new CreatePaymentCertificateDraftRequest(periodIdOnB, 1m, 0m, 3m),
                userB);
            certIdOnB = d.Id;
        }

        // Tenant A tries to look up B's certificate.
        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svcA = new PaymentCertificatesService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svcA.GetAsync(projectB, certIdOnB));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svcA.IssueAsync(projectB, certIdOnB, userA));
    }
}
