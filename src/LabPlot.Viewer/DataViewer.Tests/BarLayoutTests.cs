using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class BarLayoutTests
{
    [Fact]
    public void EstimateGroupWidth_PoolsAllSeriesAndUsesSmallestGapAboveEpsilon()
    {
        var xs = new[] { new double[] { 0, 10, 20 }, new double[] { 5, 8, 20 } };
        // 全系列をプールしてソートすると 0,5,8,10,20。隣接間隔は 5,3,2,10 で、
        // どれも epsilon (range=20 の 1e-3 = 0.02) を大きく上回るため、最小の
        // 2 (系列をまたいだ 8→10 の間隔) がそのまま groupWidth になる。
        Assert.Equal(2.0, BarLayout.EstimateGroupWidth(xs));
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
    public void EstimateGroupWidth_IgnoresRoundingLevelPairAmongEvenlySpacedPoints()
    {
        // 20 点を 1.0 間隔で並べ、その中の 1 点だけ 5e-7 離れた「丸め誤差」の
        // 双子を混ぜる。range ≈ 19、epsilon ≈ 0.019 なので 5e-7 のギャップは
        // 無視され、groupWidth は本来のカテゴリ幅 1.0 のまま保たれる
        // (旧実装はこの 1 ペアだけで groupWidth が 1.8e-7 まで潰れていた)。
        var xs = Enumerable.Range(0, 20).Select(i => (double)i).ToList();
        xs.Add(xs[10] + 5e-7);

        var groupWidth = BarLayout.EstimateGroupWidth(new[] { xs.ToArray() });

        Assert.True(Math.Abs(groupWidth - 1.0) < 1e-5, $"expected ~1.0 but was {groupWidth}");
    }

    [Fact]
    public void EstimateGroupWidth_AllGapsBelowEpsilon_FallsBackToRangeOverUniqueCount()
    {
        // 1500 点を [0,1] へ均等配置すると隣接間隔は 1/1499 (~6.7e-4) で、
        // range=1 の epsilon (1e-3) を下回るため全ギャップが「丸め誤差」として
        // 無視される。さらにその 1 点のすぐ隣に 1e-12 だけ離れた点を足し、
        // 「有効なギャップが 1 つも残らない」状態を作る。フォールバックは
        // range ÷ (一意な X の数 − 1) になり、旧来ロジック (無条件最小ギャップ)
        // なら 1e-12 まで潰れてしまう値と明確に区別できる。
        const int baseCount = 1500;
        var xs = Enumerable.Range(0, baseCount).Select(i => i / (double)(baseCount - 1)).ToList();
        xs.Add(xs[0] + 1e-12);

        var groupWidth = BarLayout.EstimateGroupWidth(new[] { xs.ToArray() });

        var uniqueCount = xs.Distinct().Count();
        var expected = 1.0 / (uniqueCount - 1);
        Assert.Equal(expected, groupWidth, 9);
        Assert.True(groupWidth > 1e-6, "fallback should not collapse to the raw rounding-level gap");
    }

    [Fact]
    public void EstimateGroupWidth_EpsilonBoundary_KeepsGapsAtOrAboveThresholdOnly()
    {
        // 0,1,...,9 (range=9, epsilon=9*1e-3=0.009) の末尾に、epsilon の
        // 105% / 95% だけ離れた点をそれぞれ追加する。105% 側はギャップが
        // epsilon 以上なので有効な最小ギャップとして採用され、95% 側は
        // 無視されて元の間隔 1.0 が groupWidth として残る。
        var baseXs = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();
        var range = baseXs[^1] - baseXs[0];
        var epsilon = range * 1e-3;

        var aboveGap = epsilon * 1.05;
        var aboveXs = baseXs.Append(baseXs[^1] + aboveGap).ToArray();
        Assert.Equal(aboveGap, BarLayout.EstimateGroupWidth(new[] { aboveXs }), 9);

        var belowGap = epsilon * 0.95;
        var belowXs = baseXs.Append(baseXs[^1] + belowGap).ToArray();
        Assert.Equal(1.0, BarLayout.EstimateGroupWidth(new[] { belowXs }), 9);
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
