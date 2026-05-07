using LabPlot.Core;

namespace DlsAnalyzer.Tests;

public sealed class PlotAppearanceTests
{
    [Fact]
    public void ComputeLegendMargin_ZeroOffset_KeepsBaselineEdgeMargin()
    {
        var margin = PlotAppearance.ComputeLegendMargin("UpperRight", 0, 0);

        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin, margin.Left);
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin, margin.Right);
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin, margin.Top);
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin, margin.Bottom);
    }

    [Theory]
    // Right-anchored: +X moves right ⇒ Right shrinks.
    [InlineData("UpperRight", 30.0, 0.0, "Right", -30.0)]
    [InlineData("LowerRight", -20.0, 0.0, "Right", +20.0)]
    // Left-anchored: +X moves right ⇒ Left grows.
    [InlineData("UpperLeft", 30.0, 0.0, "Left", +30.0)]
    [InlineData("MiddleLeft", -10.0, 0.0, "Left", -10.0)]
    public void ComputeLegendMargin_HorizontalEdge_RespectsAnchor(string position, double offsetX, double offsetY, string edge, double expectedDelta)
    {
        var margin = PlotAppearance.ComputeLegendMargin(position, offsetX, offsetY);

        var actual = edge switch
        {
            "Left" => margin.Left - PlotAppearance.DefaultLegendEdgeMargin,
            "Right" => margin.Right - PlotAppearance.DefaultLegendEdgeMargin,
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        Assert.Equal((float)expectedDelta, actual, 4);
    }

    [Theory]
    // Upper-anchored: +Y moves down ⇒ Top grows.
    [InlineData("UpperRight", 0.0, 25.0, "Top", +25.0)]
    [InlineData("UpperLeft", 0.0, -10.0, "Top", -10.0)]
    // Lower-anchored: +Y moves down ⇒ Bottom shrinks.
    [InlineData("LowerRight", 0.0, 25.0, "Bottom", -25.0)]
    [InlineData("LowerLeft", 0.0, -15.0, "Bottom", +15.0)]
    public void ComputeLegendMargin_VerticalEdge_RespectsAnchor(string position, double offsetX, double offsetY, string edge, double expectedDelta)
    {
        var margin = PlotAppearance.ComputeLegendMargin(position, offsetX, offsetY);

        var actual = edge switch
        {
            "Top" => margin.Top - PlotAppearance.DefaultLegendEdgeMargin,
            "Bottom" => margin.Bottom - PlotAppearance.DefaultLegendEdgeMargin,
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        Assert.Equal((float)expectedDelta, actual, 4);
    }

    [Fact]
    public void ComputeLegendMargin_MiddleCenter_SlidesBothEdgesSymmetrically()
    {
        var margin = PlotAppearance.ComputeLegendMargin("MiddleCenter", 20.0, 30.0);

        // +X = right ⇒ Left grows by 20, Right shrinks by 20.
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin + 20f, margin.Left);
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin - 20f, margin.Right);
        // +Y = down ⇒ Top grows by 30, Bottom shrinks by 30.
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin + 30f, margin.Top);
        Assert.Equal(PlotAppearance.DefaultLegendEdgeMargin - 30f, margin.Bottom);
    }

    [Fact]
    public void TickLengthBases_AreDoubledForHighDpiVisibility()
    {
        // 2026-05-07 doubled both bases (4f→8f / 2f→4f) so tick marks remain
        // visible on high-DPI displays. Locking the ratio (Major:Minor = 2:1)
        // here keeps any future tweak from drifting the proportion.
        Assert.Equal(8f, PlotAppearance.MajorTickLengthBase);
        Assert.Equal(4f, PlotAppearance.MinorTickLengthBase);
        Assert.Equal(2f, PlotAppearance.MajorTickLengthBase / PlotAppearance.MinorTickLengthBase);
    }
}
