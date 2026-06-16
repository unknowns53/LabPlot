using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class BarLayoutTests
{
    [Fact]
    public void EstimateGroupWidth_UsesSmallestAdjacentGap()
    {
        var xs = new[] { new double[] { 0, 10, 20 }, new double[] { 5, 8, 20 } };
        // 隣接間隔は {10,10} と {3,12,...}。全系列を通した最小の正の間隔は 3。
        Assert.Equal(3.0, BarLayout.EstimateGroupWidth(xs));
    }

    [Fact]
    public void EstimateGroupWidth_SinglePointOrAllEqual_FallsBackToOne()
    {
        Assert.Equal(1.0, BarLayout.EstimateGroupWidth(new[] { new double[] { 7 } }));
        Assert.Equal(1.0, BarLayout.EstimateGroupWidth(new[] { new double[] { 5, 5, 5 } }));
        Assert.Equal(1.0, BarLayout.EstimateGroupWidth(Array.Empty<double[]>()));
    }

    [Fact]
    public void EstimateGroupWidth_IgnoresNonFinite()
    {
        var xs = new[] { new[] { 0, double.NaN, 4, double.PositiveInfinity } };
        Assert.Equal(4.0, BarLayout.EstimateGroupWidth(xs));
    }

    [Fact]
    public void ComputeSlot_SingleSeries_CentersAndFillsGroup()
    {
        var slot = BarLayout.ComputeSlot(seriesOrdinal: 0, seriesCount: 1, groupWidth: 10);
        Assert.Equal(0.0, slot.Offset);
        // span = 10 * 0.8 = 8、slot = 8、Size = 8 * 0.9 = 7.2。
        Assert.Equal(7.2, slot.Size, 6);
    }

    [Fact]
    public void ComputeSlot_TwoSeries_DodgesSymmetricallyAboutCentre()
    {
        var left = BarLayout.ComputeSlot(0, 2, 10);
        var right = BarLayout.ComputeSlot(1, 2, 10);

        // span = 8、slot = 4。中心 (0.5) を挟んで ±2 にずれる。
        Assert.Equal(-2.0, left.Offset, 6);
        Assert.Equal(2.0, right.Offset, 6);
        Assert.Equal(left.Size, right.Size, 6);
        Assert.Equal(3.6, left.Size, 6); // 4 * 0.9
        // 2 本のバーが重ならない (中心間隔 slot=4 ≥ Size=3.6)。
        Assert.True(right.Offset - left.Offset >= left.Size);
    }

    [Fact]
    public void ComputeSlot_GuardsInvalidArguments()
    {
        var slot = BarLayout.ComputeSlot(seriesOrdinal: 0, seriesCount: 0, groupWidth: double.NaN);
        Assert.True(double.IsFinite(slot.Size) && slot.Size > 0);
        Assert.True(double.IsFinite(slot.Offset));
    }
}
