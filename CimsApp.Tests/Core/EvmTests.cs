using CimsApp.Core;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// PMBOK-textbook fixtures for <see cref="Evm"/> (T-S1-07). Uses
/// canonical worked examples from PMBOK 7 §4.4.3 / Practice Standard
/// for EVM. Decimals throughout — comparisons within a small
/// tolerance because divisions like 40/60 produce a non-terminating
/// decimal expansion. The assertion helper keeps the tolerance
/// per-test-explicit so a future reader knows what precision is
/// being claimed.
/// </summary>
public class EvmTests
{
    private static void AssertClose(decimal expected, decimal? actual,
        decimal tolerance = 0.0001m)
    {
        Assert.NotNull(actual);
        var diff = expected - actual.Value;
        if (diff < 0m) diff = -diff;
        Assert.True(diff < tolerance,
            $"Expected {expected} ± {tolerance}, got {actual.Value}");
    }

    private static void AssertClose(decimal expected, decimal actual,
        decimal tolerance = 0.0001m)
    {
        var diff = expected - actual;
        if (diff < 0m) diff = -diff;
        Assert.True(diff < tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}");
    }

    /// <summary>
    /// Canonical PMBOK over-budget / behind-schedule example.
    /// BAC = 100, planned 50% (PV = 50), earned 40% (EV = 40), spent
    /// 60% (AC = 60). Project is 20% over budget AND 10% behind
    /// schedule by the data date. Used widely as the introductory
    /// EVM worked example.
    /// </summary>
    [Fact]
    public void Calculate_pmbok_canonical_overrun_example()
    {
        var s = Evm.Calculate(pv: 50m, ev: 40m, ac: 60m, bac: 100m);

        // Variances: EV − AC and EV − PV.
        Assert.Equal(-20m, s.Cv);
        Assert.Equal(-10m, s.Sv);

        // CPI = 40/60 = 0.6667; SPI = 40/50 = 0.80.
        AssertClose(0.6667m, s.Cpi);
        AssertClose(0.8000m, s.Spi);

        // Eac (typical) = BAC / CPI = 100 / 0.6667 = 150.
        AssertClose(150m, s.Eac);
        // EacAtypical = AC + (BAC − EV) = 60 + 60 = 120.
        Assert.Equal(120m, s.EacAtypical);
        // EacScheduleAndCost = AC + (BAC − EV) / (CPI × SPI)
        //                    = 60 + 60 / (0.6667 × 0.80)
        //                    = 60 + 60 / 0.5333 = 172.5.
        AssertClose(172.5m, s.EacScheduleAndCost);

        // ETC = EAC − AC = 150 − 60 = 90; VAC = BAC − EAC = −50.
        AssertClose(90m, s.Etc);
        AssertClose(-50m, s.Vac);

        // TCPI to BAC = (BAC − EV) / (BAC − AC) = 60 / 40 = 1.5
        // (need 1.5x current efficiency to land on original budget).
        AssertClose(1.5m, s.TcpiToBac);

        // TCPI to EAC = (BAC − EV) / (EAC − AC) = 60 / 90 = 0.6667.
        // Equals CPI by construction when EAC is the typical formula —
        // a known PMBOK property, sanity-checked here.
        AssertClose(0.6667m, s.TcpiToEac);
        AssertClose(s.Cpi!.Value, s.TcpiToEac, 0.0001m);
    }

    /// <summary>
    /// Healthy project: ahead of schedule and under budget. PV=100,
    /// EV=110, AC=100, BAC=200. Both indices > 1.0.
    /// </summary>
    [Fact]
    public void Calculate_healthy_project_indices_above_one()
    {
        var s = Evm.Calculate(pv: 100m, ev: 110m, ac: 100m, bac: 200m);

        Assert.Equal(10m, s.Cv);
        Assert.Equal(10m, s.Sv);
        AssertClose(1.10m, s.Cpi);
        AssertClose(1.10m, s.Spi);

        // Eac (typical) = BAC / CPI = 200 / 1.1 ≈ 181.82.
        AssertClose(181.8182m, s.Eac);
        // EacAtypical = AC + (BAC − EV) = 100 + 90 = 190.
        Assert.Equal(190m, s.EacAtypical);
        // EacScheduleAndCost = 100 + 90 / 1.21 ≈ 174.38.
        AssertClose(174.3802m, s.EacScheduleAndCost);

        AssertClose(81.8182m, s.Etc);   // EAC − AC.
        AssertClose(18.1818m, s.Vac);   // BAC − EAC, positive (saving).

        // TCPI to BAC = 90 / 100 = 0.9 — only 0.9x current efficiency
        // needed to land on original budget.
        AssertClose(0.90m, s.TcpiToBac);
    }

    /// <summary>
    /// Behind schedule but on budget: PV=100, EV=80, AC=80, BAC=200.
    /// CPI = 1.0, SPI = 0.8.
    /// </summary>
    [Fact]
    public void Calculate_behind_schedule_on_budget()
    {
        var s = Evm.Calculate(pv: 100m, ev: 80m, ac: 80m, bac: 200m);

        Assert.Equal(0m,   s.Cv);
        Assert.Equal(-20m, s.Sv);
        AssertClose(1.0m, s.Cpi);
        AssertClose(0.8m, s.Spi);

        // Eac = BAC / CPI = 200 / 1.0 = 200 (CPI is 1, no overrun
        // forecast).
        AssertClose(200m, s.Eac);
    }

    /// <summary>
    /// Edge: AC = 0 (project hasn't spent yet). CPI undefined; the
    /// typical EAC falls back to BAC ("no signal — best estimate is
    /// the plan"). EacAtypical and TcpiToBac remain defined.
    /// </summary>
    [Fact]
    public void Calculate_zero_AC_falls_back_to_BAC_for_Eac_and_null_for_Cpi()
    {
        var s = Evm.Calculate(pv: 10m, ev: 0m, ac: 0m, bac: 100m);

        Assert.Null(s.Cpi);
        Assert.Equal(100m, s.Eac);                 // Falls back to BAC.
        Assert.Equal(100m, s.EacAtypical);         // 0 + (100 − 0).
        Assert.Null(s.EacScheduleAndCost);         // Depends on CPI.
        AssertClose(1.0m, s.TcpiToBac);            // (100−0)/(100−0).
    }

    /// <summary>
    /// Edge: PV = 0 (project not yet scheduled to start by data date).
    /// SPI undefined; EacScheduleAndCost null because it depends on
    /// SPI. CPI / typical EAC remain defined.
    /// </summary>
    [Fact]
    public void Calculate_zero_PV_yields_null_Spi_and_null_EacScheduleAndCost()
    {
        var s = Evm.Calculate(pv: 0m, ev: 5m, ac: 4m, bac: 100m);

        Assert.Null(s.Spi);
        Assert.Null(s.EacScheduleAndCost);
        AssertClose(5m / 4m, s.Cpi);               // CPI still defined.
    }

    /// <summary>
    /// Edge: BAC = AC (full budget already spent). The TCPI-to-BAC
    /// formula has zero in the denominator and must return null —
    /// "no remaining budget to spread the remaining work over."
    /// </summary>
    [Fact]
    public void Calculate_BAC_equals_AC_yields_null_TcpiToBac()
    {
        var s = Evm.Calculate(pv: 50m, ev: 40m, ac: 100m, bac: 100m);

        Assert.Null(s.TcpiToBac);
        // EAC − AC may still be non-zero, so TcpiToEac may still be
        // defined — but here EAC = BAC / CPI = 100 / 0.4 = 250 ≠ AC,
        // so TcpiToEac is defined.
        AssertClose(0.4m, s.Cpi);
        AssertClose(250m, s.Eac);
        Assert.NotNull(s.TcpiToEac);
    }

    /// <summary>
    /// Snapshot completeness: every field of the record is populated
    /// with a sensible value for a normal-case input. Future readers
    /// touching the record should be reminded by a failing test if
    /// they break wiring.
    /// </summary>
    [Fact]
    public void Calculate_snapshot_carries_inputs_verbatim()
    {
        var s = Evm.Calculate(pv: 50m, ev: 40m, ac: 60m, bac: 100m);

        Assert.Equal(50m,  s.Pv);
        Assert.Equal(40m,  s.Ev);
        Assert.Equal(60m,  s.Ac);
        Assert.Equal(100m, s.Bac);
    }

    // ── Single-formula sanity checks (lower coverage but explicit) ──

    [Theory]
    [InlineData(0,    0)]     // Both zero → CV = 0.
    [InlineData(100, 100)]    // EV == AC → CV = 0.
    [InlineData(100,  80)]    // Under spend → positive CV.
    [InlineData( 80, 100)]    // Over spend → negative CV.
    public void Cv_is_EV_minus_AC(int ev, int ac)
    {
        Assert.Equal((decimal)(ev - ac), Evm.Cv(ev, ac));
    }

    [Fact]
    public void Cpi_is_null_for_zero_AC_and_ratio_otherwise()
    {
        Assert.Null(Evm.Cpi(ev: 10m, ac: 0m));
        AssertClose(2m, Evm.Cpi(ev: 100m, ac: 50m));
    }

    [Fact]
    public void Spi_is_null_for_zero_PV_and_ratio_otherwise()
    {
        Assert.Null(Evm.Spi(ev: 10m, pv: 0m));
        AssertClose(0.5m, Evm.Spi(ev: 50m, pv: 100m));
    }

    [Fact]
    public void EacAtypical_is_AC_plus_remaining_planned_value()
    {
        // AC + (BAC − EV) = 60 + (100 − 40) = 120.
        Assert.Equal(120m, Evm.EacAtypical(ac: 60m, bac: 100m, ev: 40m));
    }
}
