using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class SeriesTransformerTests
{
    [Fact]
    public void Apply_Identity_ReturnsEqualCopy()
    {
        var source = new[] { 1.0, 2.0, double.NaN };

        var result = SeriesTransformer.Apply(source, SeriesTransform.Identity);

        Assert.Equal(1.0, result[0]);
        Assert.Equal(2.0, result[1]);
        Assert.True(double.IsNaN(result[2]));
        Assert.NotSame(source, result);
    }

    [Fact]
    public void Apply_DoesNotMutateInput()
    {
        var source = new[] { 2.0, 4.0 };

        SeriesTransformer.Apply(source, new SeriesTransform { Normalize = true, YOffset = 5 });

        Assert.Equal(new[] { 2.0, 4.0 }, source);
    }

    [Fact]
    public void Apply_Normalize_ScalesMaxAbsoluteToOne()
    {
        var result = SeriesTransformer.Apply(
            new[] { 1.0, -4.0, 2.0 },
            new SeriesTransform { Normalize = true });

        Assert.Equal(new[] { 0.25, -1.0, 0.5 }, result);
    }

    [Fact]
    public void Apply_NormalizeAllNaNOrZero_LeavesValuesUntouched()
    {
        var allNaN = SeriesTransformer.Apply(
            new[] { double.NaN, double.NaN },
            new SeriesTransform { Normalize = true });
        Assert.All(allNaN, static value => Assert.True(double.IsNaN(value)));

        var zeros = SeriesTransformer.Apply(
            new[] { 0.0, 0.0 },
            new SeriesTransform { Normalize = true });
        Assert.Equal(new[] { 0.0, 0.0 }, zeros);
    }

    [Fact]
    public void Apply_Offset_ShiftsFiniteValues()
    {
        var result = SeriesTransformer.Apply(
            new[] { 1.0, double.NaN, 3.0 },
            new SeriesTransform { YOffset = 10 });

        Assert.Equal(11.0, result[0]);
        Assert.True(double.IsNaN(result[1]));
        Assert.Equal(13.0, result[2]);
    }

    [Fact]
    public void Apply_Smoothing_AveragesCenteredWindow()
    {
        var result = SeriesTransformer.Apply(
            new[] { 0.0, 3.0, 6.0, 9.0, 12.0 },
            new SeriesTransform { SmoothingWindow = 3 });

        // 端は窓が縮む (片側のみの平均)
        Assert.Equal(1.5, result[0]);
        Assert.Equal(3.0, result[1]);
        Assert.Equal(6.0, result[2]);
        Assert.Equal(10.5, result[4]);
    }

    [Fact]
    public void Apply_EvenSmoothingWindow_RoundsUpToOdd()
    {
        var window3 = SeriesTransformer.Apply(
            new[] { 0.0, 3.0, 6.0, 9.0 },
            new SeriesTransform { SmoothingWindow = 3 });
        var window2 = SeriesTransformer.Apply(
            new[] { 0.0, 3.0, 6.0, 9.0 },
            new SeriesTransform { SmoothingWindow = 2 });

        Assert.Equal(window3, window2);
    }

    [Fact]
    public void Apply_Smoothing_ExcludesNaNFromWindowAndKeepsNaNCells()
    {
        var result = SeriesTransformer.Apply(
            new[] { 1.0, double.NaN, 3.0 },
            new SeriesTransform { SmoothingWindow = 3 });

        // NaN セルは NaN のまま、隣接セルの窓からは除外される
        Assert.Equal(1.0, result[0]);
        Assert.True(double.IsNaN(result[1]));
        Assert.Equal(3.0, result[2]);
    }

    [Fact]
    public void Apply_CombinedTransforms_RunSmoothingThenNormalizeThenOffset()
    {
        var result = SeriesTransformer.Apply(
            new[] { 0.0, 4.0, 8.0 },
            new SeriesTransform { SmoothingWindow = 3, Normalize = true, YOffset = 1 });

        // smoothing → {2,4,6} → normalize → {1/3, 2/3, 1} → offset → {4/3, 5/3, 2}
        Assert.Equal(4.0 / 3.0, result[0], precision: 12);
        Assert.Equal(5.0 / 3.0, result[1], precision: 12);
        Assert.Equal(2.0, result[2], precision: 12);
    }

    [Fact]
    public void Apply_Smoothing_MatchesNaiveWindowAverageForVariousWindows()
    {
        const int length = 500;
        var source = new double[length];
        uint seed = 12345;
        for (var i = 0; i < length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            var unit = seed / 4294967296.0; // [0, 1)

            seed = seed * 1664525 + 1013904223;
            var nanRoll = seed / 4294967296.0;

            source[i] = nanRoll < 0.1 ? double.NaN : unit * 2000.0 - 1000.0;
        }

        foreach (var window in new[] { 3, 7, 101, 100000 })
        {
            var expected = NaiveSmoothCentered(source, window);
            var actual = SeriesTransformer.Apply(source, new SeriesTransform { SmoothingWindow = window });

            Assert.Equal(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                var isBothNaN = double.IsNaN(expected[i]) && double.IsNaN(actual[i]);
                Assert.True(isBothNaN || Math.Abs(expected[i] - actual[i]) < 1e-9,
                    $"window={window}, index={i}: expected={expected[i]}, actual={actual[i]}");
            }
        }

        // 素朴な O(N×window) 中心移動平均。累積和実装 (SeriesTransformer 内) との一致確認用の参照実装。
        static double[] NaiveSmoothCentered(double[] source, int window)
        {
            if (window % 2 == 0)
            {
                window++;
            }

            var half = window / 2;
            var result = new double[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                if (double.IsNaN(source[i]))
                {
                    result[i] = double.NaN;
                    continue;
                }

                var sum = 0.0;
                var count = 0;
                var from = Math.Max(0, i - half);
                var to = Math.Min(source.Length - 1, i + half);
                for (var j = from; j <= to; j++)
                {
                    if (double.IsFinite(source[j]))
                    {
                        sum += source[j];
                        count++;
                    }
                }

                result[i] = count > 0 ? sum / count : double.NaN;
            }

            return result;
        }
    }
}
