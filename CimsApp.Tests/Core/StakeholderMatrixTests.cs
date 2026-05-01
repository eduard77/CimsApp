using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="StakeholderMatrix"/> (T-S3-04).
/// Mirrors the S2 RiskMatrixTests shape — same 5×5 grid, same
/// row-major projection, just a different domain.
/// </summary>
public class StakeholderMatrixTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 3, 6)]
    [InlineData(3, 3, 9)]
    [InlineData(4, 5, 20)]
    [InlineData(5, 5, 25)]
    public void Score_in_range_returns_P_times_I(int p, int i, int expected)
    {
        Assert.Equal(expected, StakeholderMatrix.Score(p, i));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 6)]
    public void Score_out_of_range_returns_zero(int p, int i)
    {
        Assert.Equal(0, StakeholderMatrix.Score(p, i));
    }

    [Fact]
    public void Build_returns_25_cells_in_row_major_order()
    {
        var cells = StakeholderMatrix.Build(Array.Empty<Stakeholder>());
        Assert.Equal(25, cells.Count);
        Assert.Equal(1, cells[0].Power);
        Assert.Equal(1, cells[0].Interest);
        Assert.Equal(5, cells[24].Power);
        Assert.Equal(5, cells[24].Interest);
        Assert.Equal(25, cells[24].Score);
    }

    [Fact]
    public void Build_groups_stakeholders_by_PI_coordinate()
    {
        var s1 = new Stakeholder { Id = Guid.NewGuid(), Power = 4, Interest = 5 };
        var s2 = new Stakeholder { Id = Guid.NewGuid(), Power = 4, Interest = 5 };
        var s3 = new Stakeholder { Id = Guid.NewGuid(), Power = 1, Interest = 1 };

        var cells = StakeholderMatrix.Build(new[] { s1, s2, s3 });

        var c45 = cells.Single(c => c.Power == 4 && c.Interest == 5);
        Assert.Equal(2, c45.StakeholderIds.Count);
        Assert.Contains(s1.Id, c45.StakeholderIds);
        Assert.Contains(s2.Id, c45.StakeholderIds);

        var c11 = cells.Single(c => c.Power == 1 && c.Interest == 1);
        Assert.Single(c11.StakeholderIds);
        Assert.Contains(s3.Id, c11.StakeholderIds);

        // 25 - 2 occupied = 23 empty.
        Assert.Equal(23, cells.Count(c => c.StakeholderIds.Count == 0));
    }

    [Fact]
    public void Build_drops_out_of_range_rows()
    {
        var bogus = new Stakeholder { Id = Guid.NewGuid(), Power = 99, Interest = 99 };
        var legit = new Stakeholder { Id = Guid.NewGuid(), Power = 3, Interest = 3 };

        var cells = StakeholderMatrix.Build(new[] { bogus, legit });

        Assert.Equal(25, cells.Count);
        Assert.Equal(1, cells.Sum(c => c.StakeholderIds.Count));
    }
}
