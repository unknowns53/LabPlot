using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class LegendVisibilityTests
{
    [Fact]
    public void ShouldAutoShow_SingleSeriesNoCustomName_IsFalse()
    {
        Assert.False(LegendVisibility.ShouldAutoShow(1, hasCustomLegendName: false));
    }

    [Fact]
    public void ShouldAutoShow_MultipleSeriesNoCustomName_IsTrue()
    {
        Assert.True(LegendVisibility.ShouldAutoShow(2, hasCustomLegendName: false));
    }

    [Fact]
    public void ShouldAutoShow_SingleSeriesWithCustomName_IsTrue()
    {
        Assert.True(LegendVisibility.ShouldAutoShow(1, hasCustomLegendName: true));
    }
}
