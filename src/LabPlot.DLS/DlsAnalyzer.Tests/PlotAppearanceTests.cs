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

    // 1000x600 data area used by the anchor / inverse tests below.
    // top = 0, bottom = 600 (Y grows downwards in screen coords).
    private static ScottPlot.PixelRect DataRect1000x600() => new(left: 0, right: 1000, bottom: 600, top: 0);

    [Theory]
    // Each cell of the 3x3 grid maps to one anchor. Pick centers that
    // sit unambiguously inside their cell.
    [InlineData(100, 50, "UpperLeft")]
    [InlineData(500, 50, "UpperCenter")]
    [InlineData(900, 50, "UpperRight")]
    [InlineData(100, 300, "MiddleLeft")]
    [InlineData(500, 300, "MiddleCenter")]
    [InlineData(900, 300, "MiddleRight")]
    [InlineData(100, 550, "LowerLeft")]
    [InlineData(500, 550, "LowerCenter")]
    [InlineData(900, 550, "LowerRight")]
    public void ChooseBestLegendAnchor_PicksCellByLegendCenter(float cx, float cy, string expected)
    {
        var actual = PlotAppearance.ChooseBestLegendAnchor(cx, cy, DataRect1000x600());

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("UpperLeft")]
    [InlineData("UpperCenter")]
    [InlineData("UpperRight")]
    [InlineData("MiddleLeft")]
    [InlineData("MiddleCenter")]
    [InlineData("MiddleRight")]
    [InlineData("LowerLeft")]
    [InlineData("LowerCenter")]
    [InlineData("LowerRight")]
    public void ComputeOffsetForLegendPosition_RoundTripsThroughComputeLegendMargin(string position)
    {
        var dataRect = DataRect1000x600();
        const float legendW = 200f;
        const float legendH = 80f;
        // Pick a target inside the data area that doesn't sit exactly on
        // an anchor — the inverse should still recover the offsets that
        // ComputeLegendMargin would consume to place the legend here.
        const float targetLeft = 350f;
        const float targetTop = 220f;

        var (offsetX, offsetY) = PlotAppearance.ComputeOffsetForLegendPosition(
            position, targetLeft, targetTop, legendW, legendH, dataRect);

        // Apply the computed offsets through ComputeLegendMargin and
        // forward-derive what the legend top-left would be. The two
        // pixel coordinates should round-trip to the original target
        // within float tolerance.
        var margin = PlotAppearance.ComputeLegendMargin(position, offsetX, offsetY);

        float forwardLeft;
        if (position.EndsWith("Right", StringComparison.Ordinal))
        {
            forwardLeft = dataRect.Right - margin.Right - legendW;
        }
        else if (position.EndsWith("Left", StringComparison.Ordinal))
        {
            forwardLeft = dataRect.Left + margin.Left;
        }
        else
        {
            forwardLeft = (dataRect.Left + margin.Left + dataRect.Right - margin.Right - legendW) / 2f;
        }

        float forwardTop;
        if (position.StartsWith("Upper", StringComparison.Ordinal))
        {
            forwardTop = dataRect.Top + margin.Top;
        }
        else if (position.StartsWith("Lower", StringComparison.Ordinal))
        {
            forwardTop = dataRect.Bottom - margin.Bottom - legendH;
        }
        else
        {
            forwardTop = (dataRect.Top + margin.Top + dataRect.Bottom - margin.Bottom - legendH) / 2f;
        }

        Assert.Equal(targetLeft, forwardLeft, 3);
        Assert.Equal(targetTop, forwardTop, 3);
    }

    [Fact]
    public void ComputeOffsetForLegendPosition_AtAnchorOrigin_GivesZeroOffsets()
    {
        // Place the legend exactly at the natural UpperRight anchor
        // (offset 0 → Margin = default 5 px on every side). The inverse
        // should report back (0, 0).
        var dataRect = DataRect1000x600();
        const float legendW = 200f;
        const float legendH = 80f;
        var anchorLeft = dataRect.Right - PlotAppearance.DefaultLegendEdgeMargin - legendW;
        var anchorTop = dataRect.Top + PlotAppearance.DefaultLegendEdgeMargin;

        var (dx, dy) = PlotAppearance.ComputeOffsetForLegendPosition(
            "UpperRight", anchorLeft, anchorTop, legendW, legendH, dataRect);

        Assert.Equal(0.0, dx, 3);
        Assert.Equal(0.0, dy, 3);
    }
}
