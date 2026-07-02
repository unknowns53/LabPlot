using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class SeriesPlotKindSelectorTests
{
    [Fact]
    public void IsStrictlyIncreasing_EmptyList_IsTrue()
    {
        Assert.True(SeriesPlotKindSelector.IsStrictlyIncreasing(Array.Empty<double>()));
    }

    [Fact]
    public void IsStrictlyIncreasing_SingleElement_IsTrue()
    {
        Assert.True(SeriesPlotKindSelector.IsStrictlyIncreasing(new[] { 1.0 }));
    }

    [Fact]
    public void IsStrictlyIncreasing_AscendingValues_IsTrue()
    {
        Assert.True(SeriesPlotKindSelector.IsStrictlyIncreasing(new[] { 1.0, 2.0, 3.0 }));
    }

    [Fact]
    public void IsStrictlyIncreasing_DuplicateAdjacentValues_IsFalse()
    {
        Assert.False(SeriesPlotKindSelector.IsStrictlyIncreasing(new[] { 1.0, 1.0, 2.0 }));
    }

    [Fact]
    public void IsStrictlyIncreasing_DescendingValues_IsFalse()
    {
        Assert.False(SeriesPlotKindSelector.IsStrictlyIncreasing(new[] { 3.0, 2.0, 1.0 }));
    }

    [Fact]
    public void IsStrictlyIncreasing_NonMonotonicValues_IsFalse()
    {
        Assert.False(SeriesPlotKindSelector.IsStrictlyIncreasing(new[] { 1.0, 3.0, 2.0 }));
    }

    [Fact]
    public void ShouldUseSignalXY_LineWithAscendingX_IsTrue()
    {
        Assert.True(SeriesPlotKindSelector.ShouldUseSignalXY(ViewerChartType.Line, new[] { 1.0, 2.0, 3.0 }));
    }

    [Fact]
    public void ShouldUseSignalXY_LineWithDuplicateX_IsFalse()
    {
        Assert.False(SeriesPlotKindSelector.ShouldUseSignalXY(ViewerChartType.Line, new[] { 1.0, 1.0, 2.0 }));
    }

    [Theory]
    [InlineData(ViewerChartType.Markers)]
    [InlineData(ViewerChartType.LineMarkers)]
    [InlineData(ViewerChartType.Bar)]
    public void ShouldUseSignalXY_NonLineChartTypes_AreFalseEvenWithAscendingX(ViewerChartType chartType)
    {
        Assert.False(SeriesPlotKindSelector.ShouldUseSignalXY(chartType, new[] { 1.0, 2.0, 3.0 }));
    }
}
