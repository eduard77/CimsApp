namespace CimsApp.Core;

/// <summary>
/// Earned Value Management (EVM) primitives — PMBOK 7 §4.4.3 /
/// PMBOK 6 §7.4 / PMI Practice Standard for Earned Value Management.
/// T-S1-07 (PAFM-SD F.2 fifth bullet). Pure functions: no IO, no DB,
/// no DI, no time. Inputs are project-level monetary totals at a
/// given data date; outputs are the standard EVM indices and
/// forecasts. Currency is the caller's responsibility (CIMS uses
/// Project.Currency throughout the cost domain).
///
/// Service-level integration is intentionally NOT in this task:
/// PV (planned value at data date) needs a schedule baseline
/// (T-S1-11 cashflow), and EV (earned value) needs a per-line
/// progress / percent-complete signal that v1.0 does not yet
/// capture. Shipping the math here keeps T-S1-07 honest at 8h and
/// lets the data integration land later without redoing the
/// formulas. Callers in S1 will exercise the math through these
/// pure functions plus their own PV / EV calculations.
/// </summary>
public static class Evm
{
    /// <summary>
    /// Full EVM snapshot at a single data date. All values share the
    /// project's currency; ratios are dimensionless. Ratios and
    /// forecasts that depend on a divisor that may legitimately be
    /// zero (e.g. AC, PV, BAC − AC) are nullable — null means the
    /// metric is undefined for the inputs given (typically because
    /// the project has not started spending or scheduling yet).
    /// </summary>
    public sealed record EvmSnapshot(
        decimal Pv,
        decimal Ev,
        decimal Ac,
        decimal Bac,
        decimal Cv,
        decimal Sv,
        decimal? Cpi,
        decimal? Spi,
        decimal Eac,
        decimal EacAtypical,
        decimal? EacScheduleAndCost,
        decimal Etc,
        decimal Vac,
        decimal? TcpiToBac,
        decimal? TcpiToEac);

    public static decimal Cv(decimal ev, decimal ac) => ev - ac;
    public static decimal Sv(decimal ev, decimal pv) => ev - pv;

    /// <summary>EV / AC. Null if AC is zero (no spend yet → ratio undefined).</summary>
    public static decimal? Cpi(decimal ev, decimal ac) =>
        ac == 0m ? null : ev / ac;

    /// <summary>EV / PV. Null if PV is zero (project not yet started by data date).</summary>
    public static decimal? Spi(decimal ev, decimal pv) =>
        pv == 0m ? null : ev / pv;

    /// <summary>
    /// Typical / continuing-performance EAC: BAC / CPI. The default
    /// EAC formula. Falls back to BAC when CPI is undefined or zero —
    /// no signal yet means "best estimate is still the plan".
    /// </summary>
    public static decimal Eac(decimal bac, decimal? cpi) =>
        !cpi.HasValue || cpi.Value == 0m ? bac : bac / cpi.Value;

    /// <summary>
    /// Atypical EAC: AC + (BAC − EV). "The variance was a one-off,
    /// remaining work runs at planned cost." Always defined.
    /// </summary>
    public static decimal EacAtypical(decimal ac, decimal bac, decimal ev) =>
        ac + (bac - ev);

    /// <summary>
    /// Schedule-and-cost EAC: AC + (BAC − EV) / (CPI × SPI). "Both
    /// indices continue." Null if either index is undefined or zero.
    /// </summary>
    public static decimal? EacScheduleAndCost(
        decimal ac, decimal bac, decimal ev,
        decimal? cpi, decimal? spi) =>
        (!cpi.HasValue || cpi.Value == 0m
         || !spi.HasValue || spi.Value == 0m)
            ? null
            : ac + (bac - ev) / (cpi.Value * spi.Value);

    /// <summary>
    /// To-Complete Performance Index against BAC: (BAC − EV) / (BAC − AC).
    /// The cost-efficiency factor required on remaining work to land at
    /// the original budget. Null if BAC == AC (full budget already
    /// spent — no remaining budget to spread the remaining work over).
    /// </summary>
    public static decimal? TcpiToBac(decimal bac, decimal ev, decimal ac) =>
        bac == ac ? null : (bac - ev) / (bac - ac);

    /// <summary>
    /// To-Complete Performance Index against EAC: (BAC − EV) / (EAC − AC).
    /// Required cost-efficiency to land at the revised forecast. Null
    /// if EAC == AC. When EAC is computed using the typical formula,
    /// TcpiToEac equals CPI by construction — useful sanity check.
    /// </summary>
    public static decimal? TcpiToEac(decimal eac, decimal bac, decimal ev, decimal ac) =>
        eac == ac ? null : (bac - ev) / (eac - ac);

    /// <summary>
    /// Compose a full <see cref="EvmSnapshot"/> from PV / EV / AC / BAC.
    /// </summary>
    public static EvmSnapshot Calculate(decimal pv, decimal ev, decimal ac, decimal bac)
    {
        var cpi = Cpi(ev, ac);
        var spi = Spi(ev, pv);
        var eac = Eac(bac, cpi);
        return new EvmSnapshot(
            Pv:                  pv,
            Ev:                  ev,
            Ac:                  ac,
            Bac:                 bac,
            Cv:                  Cv(ev, ac),
            Sv:                  Sv(ev, pv),
            Cpi:                 cpi,
            Spi:                 spi,
            Eac:                 eac,
            EacAtypical:         EacAtypical(ac, bac, ev),
            EacScheduleAndCost:  EacScheduleAndCost(ac, bac, ev, cpi, spi),
            Etc:                 eac - ac,
            Vac:                 bac - eac,
            TcpiToBac:           TcpiToBac(bac, ev, ac),
            TcpiToEac:           TcpiToEac(eac, bac, ev, ac));
    }
}
