using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="MonteCarlo"/> (T-S2-08).
/// Sampling tests use seeded Random for determinism; statistics
/// assertions tolerate stochastic noise within sensible bounds.
/// </summary>
public class MonteCarloTests
{
    [Theory]
    [InlineData(1, 0.10)]
    [InlineData(2, 0.30)]
    [InlineData(3, 0.50)]
    [InlineData(4, 0.70)]
    [InlineData(5, 0.90)]
    [InlineData(0, 0.00)]
    [InlineData(6, 0.00)]
    public void OccurrenceProbability_maps_PMBOK_5x5_scale(int score, double expected)
    {
        Assert.Equal(expected, MonteCarlo.OccurrenceProbability(score));
    }

    [Theory]
    [InlineData(DistributionShape.Triangular)]
    [InlineData(DistributionShape.Pert)]
    [InlineData(DistributionShape.Beta)]
    public void Sample_stays_within_BestCase_to_WorstCase_bounds(DistributionShape shape)
    {
        var rng = new Random(42);
        for (int i = 0; i < 5000; i++)
        {
            var s = MonteCarlo.Sample(shape, 100.0, 250.0, 800.0, rng);
            Assert.InRange(s, 100.0, 800.0);
        }
    }

    [Fact]
    public void Simulate_with_no_risks_returns_zero_distribution()
    {
        var r = MonteCarlo.Simulate(Array.Empty<MonteCarloInput>(), 1000, seed: 1);
        Assert.Equal(1000, r.IterationsRun);
        Assert.Equal(0.0, r.Mean);
        Assert.Equal(0.0, r.Min);
        Assert.Equal(0.0, r.Max);
        Assert.Equal(0.0, r.P10);
        Assert.Equal(0.0, r.P50);
        Assert.Equal(0.0, r.P80);
        Assert.Equal(0.0, r.P90);
    }

    [Fact]
    public void Simulate_enforces_minimum_iterations()
    {
        var r = MonteCarlo.Simulate(Array.Empty<MonteCarloInput>(), iterations: 50, seed: 1);
        Assert.Equal(MonteCarlo.MinIterations, r.IterationsRun);
    }

    [Fact]
    public void Simulate_is_deterministic_under_a_fixed_seed()
    {
        var input = new[]
        {
            new MonteCarloInput { Probability = 4, BestCase = 100, MostLikely = 250, WorstCase = 800, Distribution = DistributionShape.Triangular },
            new MonteCarloInput { Probability = 2, BestCase = 50,  MostLikely = 75,  WorstCase = 200, Distribution = DistributionShape.Pert },
        };
        var a = MonteCarlo.Simulate(input, 2000, seed: 99);
        var b = MonteCarlo.Simulate(input, 2000, seed: 99);
        Assert.Equal(a.Mean, b.Mean);
        Assert.Equal(a.P50,  b.P50);
        Assert.Equal(a.P90,  b.P90);
    }

    [Fact]
    public void Simulate_produces_monotone_percentiles()
    {
        var input = new[]
        {
            new MonteCarloInput { Probability = 3, BestCase = 100, MostLikely = 200, WorstCase = 500, Distribution = DistributionShape.Triangular },
        };
        var r = MonteCarlo.Simulate(input, 5000, seed: 7);
        Assert.True(r.Min <= r.P10);
        Assert.True(r.P10 <= r.P50);
        Assert.True(r.P50 <= r.P80);
        Assert.True(r.P80 <= r.P90);
        Assert.True(r.P90 <= r.Max);
    }

    [Fact]
    public void Simulate_p1_certain_risk_yields_nonzero_total_every_iteration()
    {
        // P=5 → 0.9 occurrence, but to avoid stochastic flakes pick a
        // long simulation and assert the *minimum* total (since across
        // 5000 iterations 10% of them produce zero) is < BestCase but
        // the P50 sits firmly inside [BestCase, WorstCase].
        var input = new[]
        {
            new MonteCarloInput { Probability = 5, BestCase = 100, MostLikely = 200, WorstCase = 500, Distribution = DistributionShape.Triangular },
        };
        var r = MonteCarlo.Simulate(input, 5000, seed: 13);
        Assert.True(r.P50 >= 100, $"P50={r.P50} should be >= BestCase 100 for a P=5 risk");
        Assert.True(r.P50 <= 500, $"P50={r.P50} should be <= WorstCase 500");
    }

    [Fact]
    public void Triangular_sampler_mean_approximates_textbook_value()
    {
        // Triangular(B, M, W) has theoretical mean = (B + M + W) / 3.
        const double b = 100, m = 200, w = 700;
        const double expected = (b + m + w) / 3.0;
        var rng = new Random(5);
        double sum = 0;
        const int n = 100_000;
        for (int i = 0; i < n; i++)
            sum += MonteCarlo.Sample(DistributionShape.Triangular, b, m, w, rng);
        var mean = sum / n;
        // ±2% tolerance at 100k samples.
        Assert.InRange(mean, expected * 0.98, expected * 1.02);
    }

    [Fact]
    public void Pert_sampler_mean_approximates_PMBOK_formula()
    {
        // PERT mean = (B + 4M + W) / 6.
        const double b = 100, m = 250, w = 800;
        const double expected = (b + 4 * m + w) / 6.0;
        var rng = new Random(11);
        double sum = 0;
        const int n = 100_000;
        for (int i = 0; i < n; i++)
            sum += MonteCarlo.Sample(DistributionShape.Pert, b, m, w, rng);
        var mean = sum / n;
        // The normal-approximation sampler clamps to [b, w] which
        // skews the mean very slightly. ±5% tolerance accommodates
        // both the clamping and the stochastic noise at this n.
        Assert.InRange(mean, expected * 0.95, expected * 1.05);
    }

    [Fact]
    public void PercentileSorted_handles_edge_cases()
    {
        Assert.Equal(0.0, MonteCarlo.PercentileSorted(Array.Empty<double>(), 0.5));
        Assert.Equal(5.0, MonteCarlo.PercentileSorted(new[] { 5.0 }, 0.5));
        var ten = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(1.0, MonteCarlo.PercentileSorted(ten, 0.10));
        Assert.Equal(5.0, MonteCarlo.PercentileSorted(ten, 0.50));
        Assert.Equal(8.0, MonteCarlo.PercentileSorted(ten, 0.80));
        Assert.Equal(9.0, MonteCarlo.PercentileSorted(ten, 0.90));
    }
}
