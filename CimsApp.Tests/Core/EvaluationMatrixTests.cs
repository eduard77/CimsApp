using CimsApp.Core;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="EvaluationMatrix"/>
/// (T-S6-05). Fixture-based: hand-crafted (criteria, scores) input
/// → expected per-tender OverallScore. No DB / DI / IO.
/// </summary>
public class EvaluationMatrixTests
{
    private static Guid G() => Guid.NewGuid();

    [Fact]
    public void Compute_returns_empty_results_for_empty_tender_set()
    {
        var r = EvaluationMatrix.Compute([], [], []);
        Assert.Empty(r.Tenders);
        Assert.Equal(0m, r.TotalWeight);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Compute_validates_weight_sum_at_1()
    {
        var c1 = G(); var c2 = G();
        var r = EvaluationMatrix.Compute(
            [],
            [new(c1, 0.6m), new(c2, 0.4m)],
            []);
        Assert.Equal(1.0m, r.TotalWeight);
        Assert.True(r.IsValid);
    }

    [Theory]
    [InlineData(0.85)]
    [InlineData(1.15)]
    [InlineData(0.5)]
    public void Compute_flags_invalid_weight_sum(decimal sum)
    {
        var c1 = G();
        var r = EvaluationMatrix.Compute(
            [],
            [new(c1, sum)],
            []);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Compute_single_criterion_single_tender_full_score()
    {
        // Criterion weight 1.0, score 80 → overall 80.
        var c1 = G(); var t1 = G();
        var r = EvaluationMatrix.Compute(
            [t1],
            [new(c1, 1.0m)],
            [new(t1, c1, 80m)]);
        var row = r.Tenders.Single();
        Assert.Equal(t1,    row.TenderId);
        Assert.Equal(80m,   row.OverallScore);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Compute_two_criteria_weighted_average()
    {
        // 60% Price (score 90) + 40% Quality (score 70) = 54 + 28 = 82.
        var price = G(); var quality = G(); var t = G();
        var r = EvaluationMatrix.Compute(
            [t],
            [new(price, 0.6m), new(quality, 0.4m)],
            [new(t, price, 90m), new(t, quality, 70m)]);
        Assert.Equal(82m, r.Tenders.Single().OverallScore);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Compute_returns_null_OverallScore_when_score_missing_for_any_criterion()
    {
        // Two criteria, one scored, one not → null.
        var c1 = G(); var c2 = G(); var t = G();
        var r = EvaluationMatrix.Compute(
            [t],
            [new(c1, 0.5m), new(c2, 0.5m)],
            [new(t, c1, 80m)]);   // no score for c2
        Assert.Null(r.Tenders.Single().OverallScore);
    }

    [Fact]
    public void Compute_multiple_tenders_each_independently_aggregated()
    {
        var c1 = G(); var c2 = G();
        var ta = G(); var tb = G(); var tc = G();
        var r = EvaluationMatrix.Compute(
            [ta, tb, tc],
            [new(c1, 0.5m), new(c2, 0.5m)],
            [
                new(ta, c1, 100m), new(ta, c2,  60m),  // overall 80
                new(tb, c1,  80m), new(tb, c2,  80m),  // overall 80
                new(tc, c1,  90m), new(tc, c2, 100m),  // overall 95
            ]);
        var tenderA = r.Tenders.Single(x => x.TenderId == ta);
        var tenderB = r.Tenders.Single(x => x.TenderId == tb);
        var tenderC = r.Tenders.Single(x => x.TenderId == tc);
        Assert.Equal(80m, tenderA.OverallScore);
        Assert.Equal(80m, tenderB.OverallScore);
        Assert.Equal(95m, tenderC.OverallScore);
    }

    [Fact]
    public void Compute_three_criteria_PMBOK_textbook_weighted_sum()
    {
        // Classic example: 50% Price (88), 30% Quality (75), 20% Delivery (90).
        // 0.5*88 + 0.3*75 + 0.2*90 = 44 + 22.5 + 18 = 84.5.
        var price = G(); var quality = G(); var delivery = G(); var t = G();
        var r = EvaluationMatrix.Compute(
            [t],
            [new(price, 0.5m), new(quality, 0.3m), new(delivery, 0.2m)],
            [new(t, price, 88m), new(t, quality, 75m), new(t, delivery, 90m)]);
        Assert.Equal(84.5m, r.Tenders.Single().OverallScore);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Compute_with_invalid_weight_sum_still_returns_per_tender_results()
    {
        // Weights sum to 0.5; OverallScore is computed but caller
        // should distrust per IsValid = false.
        var c1 = G(); var t = G();
        var r = EvaluationMatrix.Compute(
            [t],
            [new(c1, 0.5m)],
            [new(t, c1, 80m)]);
        Assert.False(r.IsValid);
        Assert.Equal(40m, r.Tenders.Single().OverallScore);   // 0.5 * 80
    }

    [Fact]
    public void Compute_zero_criteria_yields_zero_overall_for_every_tender()
    {
        // No criteria → no work to do; OverallScore = 0 (sum of empty
        // set) for every tender. IsValid = false (TotalWeight = 0).
        var t1 = G(); var t2 = G();
        var r = EvaluationMatrix.Compute([t1, t2], [], []);
        Assert.False(r.IsValid);
        Assert.All(r.Tenders, x => Assert.Equal(0m, x.OverallScore));
    }
}
