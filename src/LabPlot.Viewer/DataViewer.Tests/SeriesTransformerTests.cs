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
}
