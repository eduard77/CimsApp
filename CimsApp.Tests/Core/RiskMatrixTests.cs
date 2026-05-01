using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="RiskMatrix"/> (T-S2-05).
/// No DB, no DI; just the math + cell projection.
/// </summary>
public class RiskMatrixTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 3, 6)]
    [InlineData(3, 3, 9)]
    [InlineData(4, 5, 20)]
    [InlineData(5, 5, 25)]
    public void Score_in_range_returns_P_times_I(int p, int i, int expected)
    {
        Assert.Equal(expected, RiskMatrix.Score(p, i));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 6)]
    [InlineData(-1, -1)]
    public void Score_out_of_range_returns_zero(int p, int i)
    {
        Assert.Equal(0, RiskMatrix.Score(p, i));
    }

    [Fact]
    public void Build_returns_25_cells_in_row_major_order()
    {
        var cells = RiskMatrix.Build(Array.Empty<Risk>());
        Assert.Equal(25, cells.Count);
        // Row-major: (1,1), (1,2), ..., (1,5), (2,1), ..., (5,5)
        Assert.Equal(1, cells[0].Probability);
        Assert.Equal(1, cells[0].Impact);
        Assert.Equal(1, cells[0].Score);
        Assert.Equal(1, cells[4].Probability);
        Assert.Equal(5, cells[4].Impact);
        Assert.Equal(5, cells[4].Score);
        Assert.Equal(2, cells[5].Probability);
        Assert.Equal(1, cells[5].Impact);
        Assert.Equal(5, cells[24].Probability);
        Assert.Equal(5, cells[24].Impact);
        Assert.Equal(25, cells[24].Score);
    }

    [Fact]
    public void Build_empty_input_yields_25_empty_cells()
    {
        var cells = RiskMatrix.Build(Array.Empty<Risk>());
        Assert.All(cells, c => Assert.Empty(c.RiskIds));
    }

    [Fact]
    public void Build_groups_risks_by_PI_coordinate()
    {
        var r1 = new Risk { Id = Guid.NewGuid(), Probability = 4, Impact = 5, Score = 20 };
        var r2 = new Risk { Id = Guid.NewGuid(), Probability = 4, Impact = 5, Score = 20 };
        var r3 = new Risk { Id = Guid.NewGuid(), Probability = 1, Impact = 1, Score = 1 };

        var cells = RiskMatrix.Build(new[] { r1, r2, r3 });

        var c45 = cells.Single(c => c.Probability == 4 && c.Impact == 5);
        Assert.Equal(2, c45.RiskIds.Count);
        Assert.Contains(r1.Id, c45.RiskIds);
        Assert.Contains(r2.Id, c45.RiskIds);

        var c11 = cells.Single(c => c.Probability == 1 && c.Impact == 1);
        Assert.Single(c11.RiskIds);
        Assert.Contains(r3.Id, c11.RiskIds);

        // 25 cells total - 2 occupied (one at (4,5), one at (1,1)) = 23 empty
        Assert.Equal(23, cells.Count(c => c.RiskIds.Count == 0));
    }

    [Fact]
    public void Build_silently_drops_risks_with_out_of_range_probability_or_impact()
    {
        // Defensive — service layer prevents this from being persisted,
        // but if it ever happens, the matrix should not crash or
        // include the row.
        var bogus = new Risk { Id = Guid.NewGuid(), Probability = 99, Impact = 99 };
        var legit = new Risk { Id = Guid.NewGuid(), Probability = 3, Impact = 3, Score = 9 };

        var cells = RiskMatrix.Build(new[] { bogus, legit });

        Assert.Equal(25, cells.Count);
        Assert.Equal(1, cells.Sum(c => c.RiskIds.Count));
        var c33 = cells.Single(c => c.Probability == 3 && c.Impact == 3);
        Assert.Contains(legit.Id, c33.RiskIds);
    }
}
