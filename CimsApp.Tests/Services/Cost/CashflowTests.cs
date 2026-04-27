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
/// Behavioural tests for the T-S1-11 cashflow S-curve:
/// `CostService.SetPeriodBaselineAsync` and
/// `CostService.GetCashflowAsync`. Project-level only in v1.0;
/// per-CBS-line breakdown deferred. Forecast formula: actuals
/// up to the latest period with any actuals, then
/// baseline-projected from there.
/// </summary>
public class CashflowTests
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

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<Guid> SeedLineAsync(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid projectId)
    {
        var lineId = Guid.NewGuid();
        using var seed = new CimsDbContext(options, tenant);
        seed.CostBreakdownItems.Add(new CostBreakdownItem
        {
            Id = lineId, ProjectId = projectId, Code = "1", Name = "Root",
        });
        seed.SaveChanges();
        return await Task.FromResult(lineId);
    }

    [Fact]
    public async Task CreatePeriod_with_planned_cashflow_persists_baseline()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid periodId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), 100_000m),
                userId);
            periodId = p.Id;
            Assert.Equal(100_000m, p.PlannedCashflow);
        }

        using var verify = new CimsDbContext(options, tenant);
        Assert.Equal(100_000m, verify.CostPeriods.Single(p => p.Id == periodId).PlannedCashflow);
    }

    [Fact]
    public async Task SetPeriodBaseline_updates_value_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid periodId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30)),
                userId);
            periodId = p.Id;
            // First set: previous null → 50_000.
            await svc.SetPeriodBaselineAsync(projectId, periodId,
                new SetPeriodBaselineRequest(50_000m), userId);
            // Update: 50_000 → 75_000.
            await svc.SetPeriodBaselineAsync(projectId, periodId,
                new SetPeriodBaselineRequest(75_000m), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        Assert.Equal(75_000m, verify.CostPeriods.Single(p => p.Id == periodId).PlannedCashflow);

        var audits = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "cost_period.baseline_set")
            .OrderBy(a => a.CreatedAt).ToList();
        Assert.Equal(2, audits.Count);
        Assert.Contains("\"previous\":null", audits[0].Detail);
        Assert.Contains("\"current\":50000", audits[0].Detail);
        Assert.Contains("\"previous\":50000", audits[1].Detail);
        Assert.Contains("\"current\":75000", audits[1].Detail);
    }

    [Fact]
    public async Task SetPeriodBaseline_negative_value_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var p = await svc.CreatePeriodAsync(projectId,
            new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30)),
            userId);
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.SetPeriodBaselineAsync(projectId, p.Id,
                new SetPeriodBaselineRequest(-1m), userId));
    }

    [Fact]
    public async Task SetPeriodBaseline_period_in_wrong_project_is_NotFound()
    {
        var (options, tenant, orgId, userId, projectA) = BuildFixture();
        var projectB = Guid.NewGuid();
        Guid periodOnB;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            seed.SaveChanges();
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var p = await svc.CreatePeriodAsync(projectB,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30)),
                userId);
            periodOnB = p.Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.SetPeriodBaselineAsync(projectA, periodOnB,
                new SetPeriodBaselineRequest(100m), userId));
    }

    [Fact]
    public async Task GetCashflow_with_no_periods_returns_empty_curve()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var dto = await svc.GetCashflowAsync(projectId);

        Assert.Equal("GBP", dto.ProjectCurrency);
        Assert.Empty(dto.Points);
    }

    [Fact]
    public async Task GetCashflow_baseline_only_yields_cumulative_baseline_and_baseline_forecast()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), 100m), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("May", Utc(2026, 5, 1), Utc(2026, 5, 31), 200m), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Jun", Utc(2026, 6, 1), Utc(2026, 6, 30), 300m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowAsync(projectId);

        Assert.Equal(3, dto.Points.Count);
        Assert.Equal(100m, dto.Points[0].CumulativeBaseline);
        Assert.Equal(300m, dto.Points[1].CumulativeBaseline);
        Assert.Equal(600m, dto.Points[2].CumulativeBaseline);
        // No actuals → cumulative actual is 0 everywhere.
        Assert.All(dto.Points, p => Assert.Equal(0m, p.CumulativeActual));
        // Forecast with no actuals: full baseline curve (latestActualIdx = -1).
        Assert.Equal(100m, dto.Points[0].Forecast);
        Assert.Equal(300m, dto.Points[1].Forecast);
        Assert.Equal(600m, dto.Points[2].Forecast);
    }

    [Fact]
    public async Task GetCashflow_actuals_only_yields_cumulative_actual_and_actuals_only_forecast()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = await SeedLineAsync(options, tenant, projectId);

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var apr = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30)), userId);
            var may = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("May", Utc(2026, 5, 1), Utc(2026, 5, 31)), userId);
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, apr.Id, 50m, null, null), userId);
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, may.Id, 70m, null, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowAsync(projectId);

        Assert.Equal(2, dto.Points.Count);
        Assert.Equal(50m,  dto.Points[0].CumulativeActual);
        Assert.Equal(120m, dto.Points[1].CumulativeActual);
        Assert.Equal(0m, dto.Points[0].CumulativeBaseline);
        Assert.Equal(0m, dto.Points[1].CumulativeBaseline);
        // Forecast = cumulative actual up through latest actual period;
        // both periods have actuals so forecast == cumulative actual.
        Assert.Equal(50m,  dto.Points[0].Forecast);
        Assert.Equal(120m, dto.Points[1].Forecast);
    }

    [Fact]
    public async Task GetCashflow_baseline_and_actuals_forecast_bridges_actuals_with_baseline_projection()
    {
        // Apr / May have actuals; Jun is in the future (no actuals) but has
        // a baseline. Forecast for Jun should be: cumulative actual at May
        // (the latest actual period) + (cumulative baseline at Jun −
        // cumulative baseline at May). May = 100 actual cumulative.
        // Cumulative baseline: Apr=80, May=160, Jun=240. Forecast at Jun
        // = 100 + (240 − 160) = 180.
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var lineId = await SeedLineAsync(options, tenant, projectId);

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var apr = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), 80m), userId);
            var may = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("May", Utc(2026, 5, 1), Utc(2026, 5, 31), 80m), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Jun", Utc(2026, 6, 1), Utc(2026, 6, 30), 80m), userId);

            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, apr.Id, 40m, null, null), userId);
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineId, may.Id, 60m, null, null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowAsync(projectId);

        Assert.Equal(3, dto.Points.Count);

        // Apr (past actual): forecast = cumulative actual = 40.
        Assert.Equal(40m, dto.Points[0].CumulativeActual);
        Assert.Equal(40m, dto.Points[0].Forecast);
        // May (latest actual): forecast = cumulative actual = 100.
        Assert.Equal(100m, dto.Points[1].CumulativeActual);
        Assert.Equal(100m, dto.Points[1].Forecast);
        // Jun (future, has baseline): bridged forecast = 100 + (240 − 160) = 180.
        Assert.Equal(100m, dto.Points[2].CumulativeActual);    // No actual posted yet.
        Assert.Equal(240m, dto.Points[2].CumulativeBaseline);
        Assert.Equal(180m, dto.Points[2].Forecast);
    }

    [Fact]
    public async Task GetCashflow_orders_points_by_StartDate()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            // Insert Jun first, then Apr, then May — test that GetCashflow
            // sorts by StartDate not by insertion order.
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Jun", Utc(2026, 6, 1), Utc(2026, 6, 30), 30m), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", Utc(2026, 4, 1), Utc(2026, 4, 30), 10m), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("May", Utc(2026, 5, 1), Utc(2026, 5, 31), 20m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowAsync(projectId);

        Assert.Equal(new[] { "Apr", "May", "Jun" },
            dto.Points.Select(p => p.Label).ToArray());
        // Cumulative chain follows the StartDate order, not insertion order.
        Assert.Equal(10m, dto.Points[0].CumulativeBaseline);
        Assert.Equal(30m, dto.Points[1].CumulativeBaseline);
        Assert.Equal(60m, dto.Points[2].CumulativeBaseline);
    }

    // ── T-S1-11 wire-up via B-017: per-CBS-line cashflow ─────────────

    [Fact]
    public async Task GetCashflowByLine_distributes_budget_across_overlapping_periods()
    {
        // Single line, Budget 91, scheduled Apr 1 → Jul 1 (91 days).
        // Three monthly periods Apr / May / Jun, lengths 30 / 31 / 30
        // days. Distribution = Budget × (period-overlap-days /
        // schedule-total-days). Σ across all three periods equals
        // Budget exactly because the schedule and the union of
        // periods coincide.
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var apr1  = Utc(2026, 4, 1);
        var may1  = Utc(2026, 5, 1);
        var jun1  = Utc(2026, 6, 1);
        var jul1  = Utc(2026, 7, 1);

        Guid lineId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var l = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "1", Name = "L1",
                Budget = 91m,
                ScheduledStart = apr1, ScheduledEnd = jul1,
            };
            seed.CostBreakdownItems.Add(l);
            seed.SaveChanges();
            lineId = l.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", apr1, may1), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("May", may1, jun1), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Jun", jun1, jul1), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowByLineAsync(projectId);

        var line = Assert.Single(dto.Lines);
        Assert.Equal(lineId, line.ItemId);
        Assert.Equal(3, line.Points.Count);

        // Decimal repeated-division drift: 91 × (30/91) does not close
        // exactly because 30/91 has a non-terminating decimal expansion.
        // The math is conservation-correct; the test asserts within a
        // tight tolerance.
        static void AssertClose(decimal expected, decimal actual, decimal tol = 0.0001m)
        {
            var diff = expected - actual;
            if (diff < 0m) diff = -diff;
            Assert.True(diff < tol, $"Expected {expected} ± {tol}, got {actual}");
        }

        // Σ across periods ≈ Budget (perfectly-aligned schedule).
        AssertClose(91m, line.Points.Sum(p => p.BaselinePlanned));

        // Apr 30/91, May 31/91, Jun 30/91.
        AssertClose(30m, line.Points[0].BaselinePlanned);
        AssertClose(31m, line.Points[1].BaselinePlanned);
        AssertClose(30m, line.Points[2].BaselinePlanned);
    }

    [Fact]
    public async Task GetCashflowByLine_perfectly_aligned_schedule_sums_to_full_Budget()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var apr1 = Utc(2026, 4, 1);
        var may1 = Utc(2026, 5, 1);
        var jun1 = Utc(2026, 6, 1);

        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.CostBreakdownItems.Add(new CostBreakdownItem
            {
                ProjectId = projectId, Code = "1", Name = "L1",
                Budget = 6_000m,
                ScheduledStart = apr1, ScheduledEnd = jun1,
            });
            seed.SaveChanges();
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", apr1, may1), userId);
            await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("May", may1, jun1), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowByLineAsync(projectId);
        var line = Assert.Single(dto.Lines);
        var sum = line.Points.Sum(p => p.BaselinePlanned);
        Assert.Equal(6_000m, sum);   // Schedule covers exactly Apr + May.
    }

    [Fact]
    public async Task GetCashflowByLine_actuals_attach_to_correct_line_period_pair()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var apr1 = Utc(2026, 4, 1);
        var may1 = Utc(2026, 5, 1);

        Guid lineAId, lineBId, periodAprId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var lA = new CostBreakdownItem { ProjectId = projectId, Code = "A", Name = "A", Budget = 100m };
            var lB = new CostBreakdownItem { ProjectId = projectId, Code = "B", Name = "B", Budget = 200m };
            seed.CostBreakdownItems.AddRange(lA, lB);
            seed.SaveChanges();
            lineAId = lA.Id; lineBId = lB.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var apr = await svc.CreatePeriodAsync(projectId,
                new CreatePeriodRequest("Apr", apr1, may1), userId);
            periodAprId = apr.Id;
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineAId, apr.Id, 25m, "INV-A", null), userId);
            await svc.RecordActualAsync(projectId,
                new RecordActualRequest(lineBId, apr.Id, 75m, "INV-B", null), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CostService(db2, new AuditService(db2));
        var dto = await svc2.GetCashflowByLineAsync(projectId);

        var aSeries = dto.Lines.Single(l => l.ItemId == lineAId);
        var bSeries = dto.Lines.Single(l => l.ItemId == lineBId);
        Assert.Equal(25m, aSeries.Points.Single(p => p.PeriodId == periodAprId).Actual);
        Assert.Equal(75m, bSeries.Points.Single(p => p.PeriodId == periodAprId).Actual);
        // Lines without schedules → baseline 0 across all periods.
        Assert.All(aSeries.Points, p => Assert.Equal(0m, p.BaselinePlanned));
        Assert.All(bSeries.Points, p => Assert.Equal(0m, p.BaselinePlanned));
    }

    [Fact]
    public async Task GetCashflowByLine_cross_tenant_project_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.GetCashflowByLineAsync(projectB));
    }

    [Fact]
    public async Task GetCashflow_cross_tenant_project_is_NotFound()
    {
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svcA = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svcA.GetCashflowAsync(projectB));
    }
}
