using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// Cost-side Monte Carlo simulation primitives for the Risk module
/// (T-S2-08, PAFM-SD F.3 fourth bullet — cost half only per CR-004;
/// schedule-side MC is v1.1 / B-028). Pure functions: no IO, no DB,
/// no DI, no time. Inputs are quantified risks (Probability + 3-point
/// estimate + Distribution); outputs are percentile statistics on
/// the simulated total-cost distribution.
///
/// Probability scale (PMBOK convention, hard-coded for v1.0; per-tenant
/// override is S14 territory per the S2 kickoff Top-3-risks mitigation):
///   P = 1 → 0.10
///   P = 2 → 0.30
///   P = 3 → 0.50
///   P = 4 → 0.70
///   P = 5 → 0.90
///
/// Distribution sampling:
///   Triangular: closed-form inverse CDF.
///   Pert / Beta: PERT μ = (B + 4M + W) / 6, σ = (W - B) / 6,
///                normal-approximation samples (Box-Muller). The exact
///                Beta sampler (Marsaglia-Tsang Gamma + ratio) is
///                deferred to v1.1 if a real workflow demands tail
///                accuracy; for percentile spreads at 1000+ iterations
///                the normal approximation tracks the textbook
///                Beta(α, β) within a few percent.
///
/// Beta is treated identically to Pert in v1.0 — explicit (α, β)
/// parameterisation is a v1.1 candidate.
/// </summary>
public static class MonteCarlo
{
    public const int MinIterations = 1000;

    /// <summary>Map the 1..5 risk-matrix probability to an occurrence
    /// probability in [0, 1]. Out-of-range inputs return 0.</summary>
    public static double OccurrenceProbability(int probabilityScore) => probabilityScore switch
    {
        1 => 0.10,
        2 => 0.30,
        3 => 0.50,
        4 => 0.70,
        5 => 0.90,
        _ => 0.0,
    };

    /// <summary>Sample one realisation of the cost impact for a risk
    /// that has occurred. Inputs are validated by the service layer
    /// (BestCase ≤ MostLikely ≤ WorstCase, all non-negative). Caller
    /// supplies the Random instance for determinism control.</summary>
    public static double Sample(DistributionShape shape, double best, double mostLikely, double worst, Random rng)
    {
        if (worst <= best) return mostLikely; // degenerate — single-point estimate
        return shape switch
        {
            DistributionShape.Triangular => SampleTriangular(best, mostLikely, worst, rng),
            DistributionShape.Pert       => SamplePertNormalApprox(best, mostLikely, worst, rng),
            DistributionShape.Beta       => SamplePertNormalApprox(best, mostLikely, worst, rng),
            _ => mostLikely,
        };
    }

    /// <summary>
    /// Run the cost-side simulation. Each iteration: every risk is
    /// either drawn (with occurrence probability per its matrix
    /// score) or not; if drawn, the cost impact is sampled from its
    /// Distribution; per-iteration totals across risks are
    /// accumulated. Output gives min/mean/max plus percentiles
    /// P10/P50/P80/P90 of the total-cost distribution.
    /// </summary>
    public static MonteCarloResult Simulate(IReadOnlyList<MonteCarloInput> risks, int iterations, int seed)
    {
        if (iterations < MinIterations) iterations = MinIterations;

        var rng = new Random(seed);
        var totals = new double[iterations];
        for (int it = 0; it < iterations; it++)
        {
            double total = 0.0;
            foreach (var r in risks)
            {
                var p = OccurrenceProbability(r.Probability);
                if (p <= 0) continue;
                if (rng.NextDouble() >= p) continue;
                total += Sample(r.Distribution, r.BestCase, r.MostLikely, r.WorstCase, rng);
            }
            totals[it] = total;
        }

        Array.Sort(totals);
        return new MonteCarloResult
        {
            IterationsRun = iterations,
            Min   = totals[0],
            Max   = totals[iterations - 1],
            Mean  = MeanOf(totals),
            P10   = PercentileSorted(totals, 0.10),
            P50   = PercentileSorted(totals, 0.50),
            P80   = PercentileSorted(totals, 0.80),
            P90   = PercentileSorted(totals, 0.90),
        };
    }

    /// <summary>Public for testability; nearest-rank percentile on a
    /// pre-sorted array.</summary>
    public static double PercentileSorted(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0.0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Length) idx = sorted.Length - 1;
        return sorted[idx];
    }

    private static double MeanOf(double[] xs)
    {
        double sum = 0;
        for (int i = 0; i < xs.Length; i++) sum += xs[i];
        return xs.Length == 0 ? 0 : sum / xs.Length;
    }

    private static double SampleTriangular(double b, double m, double w, Random rng)
    {
        var u = rng.NextDouble();
        var c = (m - b) / (w - b);
        if (u < c) return b + Math.Sqrt(u * (w - b) * (m - b));
        return w - Math.Sqrt((1 - u) * (w - b) * (w - m));
    }

    private static double SamplePertNormalApprox(double b, double m, double w, Random rng)
    {
        var mean = (b + 4 * m + w) / 6.0;
        var std  = (w - b) / 6.0;
        var z    = NextStandardNormal(rng);
        var s    = mean + std * z;
        // Clamp to [b, w]: the normal approximation can stray outside
        // the support; clamping preserves the contract that an
        // occurred risk's cost lies in the analyst's stated range.
        if (s < b) s = b;
        if (s > w) s = w;
        return s;
    }

    private static double NextStandardNormal(Random rng)
    {
        // Box-Muller; ignores the second sample (caching it would help
        // throughput at 10× iterations, accept the cost for v1.0).
        var u1 = 1.0 - rng.NextDouble(); // (0, 1]
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>One simulation input — a quantified Risk projected onto
/// the simulator's value type. Constructed from a Risk row by the
/// service layer (which filters out unquantified risks).</summary>
public sealed class MonteCarloInput
{
    public int Probability { get; init; }              // 1..5
    public double BestCase { get; init; }
    public double MostLikely { get; init; }
    public double WorstCase { get; init; }
    public DistributionShape Distribution { get; init; }
}

/// <summary>Aggregate output from a simulation run. All values are in
/// the project's currency (caller's responsibility — not stored).</summary>
public sealed class MonteCarloResult
{
    public int IterationsRun { get; init; }
    public double Min  { get; init; }
    public double Mean { get; init; }
    public double Max  { get; init; }
    public double P10  { get; init; }
    public double P50  { get; init; }
    public double P80  { get; init; }
    public double P90  { get; init; }
}
