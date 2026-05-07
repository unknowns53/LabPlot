using DlsAnalyzer.Core;
using LabPlot.Core;

namespace DlsAnalyzer.Tests;

public sealed class GraphFormattingConfigTests
{
    [Fact]
    public void Defaults_AreSetForAllDlsSpecificFields()
    {
        var config = new GraphFormattingConfig();

        Assert.Null(config.XAxisMode);
        Assert.Equal(0.1, config.XAxisMinNm);
        Assert.Equal(10000.0, config.XAxisMaxNm);

        Assert.Null(config.YAxisMode);
        Assert.Equal(0.0, config.YAxisMinPercent);
        Assert.Equal(30.0, config.YAxisMaxPercent);

        Assert.Null(config.LegendVisibility);
        Assert.Equal("UpperRight", config.LegendPosition);

        Assert.Equal("Number", config.DefaultDistributionMode);
        Assert.Equal(0, config.DefaultRunIndex);
    }

    [Fact]
    public void CreateFactoryDefault_ReturnsFreshInstance()
    {
        var a = GraphFormattingConfig.CreateFactoryDefault();
        var b = GraphFormattingConfig.CreateFactoryDefault();

        Assert.NotSame(a, b);
        Assert.Equal(a.XAxisMinNm, b.XAxisMinNm);
        Assert.Equal(a.LegendPosition, b.LegendPosition);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("Auto", null)]
    [InlineData("auto", null)]
    [InlineData("Manual", "Manual")]
    [InlineData("manual", "Manual")]
    [InlineData("Bogus", null)]
    public void Normalize_AxisMode_AcceptsKnownValues(string? input, string? expected)
    {
        var x = new GraphFormattingConfig { XAxisMode = input };
        var y = new GraphFormattingConfig { YAxisMode = input };

        x.Normalize();
        y.Normalize();

        Assert.Equal(expected, x.XAxisMode);
        Assert.Equal(expected, y.YAxisMode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Auto", null)]
    [InlineData("auto", null)]
    [InlineData("Always", "Always")]
    [InlineData("ALWAYS", "Always")]
    [InlineData("Never", "Never")]
    [InlineData("Bogus", null)]
    public void Normalize_LegendVisibility_AcceptsKnownValues(string? input, string? expected)
    {
        var config = new GraphFormattingConfig { LegendVisibility = input };

        config.Normalize();

        Assert.Equal(expected, config.LegendVisibility);
    }

    [Theory]
    [InlineData("UpperRight", "UpperRight")]
    [InlineData("upperright", "UpperRight")]
    [InlineData("UpperLeft", "UpperLeft")]
    [InlineData("LowerRight", "LowerRight")]
    [InlineData("LowerLeft", "LowerLeft")]
    [InlineData("MiddleRight", "MiddleRight")]
    [InlineData("middleright", "MiddleRight")]
    [InlineData(null, "UpperRight")]
    [InlineData("OutsideRight", "UpperRight")]
    [InlineData("Bogus", "UpperRight")]
    public void Normalize_LegendPosition_FallsBackToDefault(string? input, string expected)
    {
        var config = new GraphFormattingConfig { LegendPosition = input ?? string.Empty };

        config.Normalize();

        Assert.Equal(expected, config.LegendPosition);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.1, 0.1)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.0, GraphFormattingConfigBase.DefaultTickDensity)]
    [InlineData(-0.3, GraphFormattingConfigBase.DefaultTickDensity)]
    [InlineData(0.05, GraphFormattingConfigBase.DefaultTickDensity)]
    [InlineData(2.5, GraphFormattingConfigBase.DefaultTickDensity)]
    [InlineData(double.NaN, GraphFormattingConfigBase.DefaultTickDensity)]
    [InlineData(double.PositiveInfinity, GraphFormattingConfigBase.DefaultTickDensity)]
    public void Normalize_TickDensity_ClampsOrFallsBack(double input, double expected)
    {
        var config = new GraphFormattingConfig { TickDensity = input };

        config.Normalize();

        Assert.Equal(expected, config.TickDensity);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(3.0, 3.0)]
    [InlineData(0.0, GraphFormattingConfigBase.DefaultTickWidth)]
    [InlineData(-0.5, GraphFormattingConfigBase.DefaultTickWidth)]
    [InlineData(double.NaN, GraphFormattingConfigBase.DefaultTickWidth)]
    [InlineData(double.PositiveInfinity, GraphFormattingConfigBase.DefaultTickWidth)]
    public void Normalize_TickWidth_FallsBackOnNonPositive(double input, double expected)
    {
        var config = new GraphFormattingConfig { TickWidth = input };

        config.Normalize();

        Assert.Equal(expected, config.TickWidth);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(10.5, 10.5)]
    public void Normalize_LegendFontSize_AllowsPositiveOverrideOnly(double? input, double? expected)
    {
        var config = new GraphFormattingConfig { LegendFontSize = input };

        config.Normalize();

        Assert.Equal(expected, config.LegendFontSize);
    }

    [Theory]
    [InlineData("Number", "Number")]
    [InlineData("number", "Number")]
    [InlineData("Intensity", "Intensity")]
    [InlineData("Volume", "Volume")]
    [InlineData(null, "Number")]
    [InlineData("Bogus", "Number")]
    public void Normalize_DistributionMode_FallsBackToNumber(string? input, string expected)
    {
        var config = new GraphFormattingConfig { DefaultDistributionMode = input ?? string.Empty };

        config.Normalize();

        Assert.Equal(expected, config.DefaultDistributionMode);
    }

    [Fact]
    public void Normalize_ResetsNonPositiveXAxisEndpointsToDefaults()
    {
        var config = new GraphFormattingConfig
        {
            XAxisMinNm = -1.0,
            XAxisMaxNm = double.NaN,
        };

        config.Normalize();

        Assert.Equal(0.1, config.XAxisMinNm);
        Assert.Equal(10000.0, config.XAxisMaxNm);
    }

    [Fact]
    public void Normalize_ResetsInvertedXAxisRange()
    {
        var config = new GraphFormattingConfig
        {
            XAxisMinNm = 5000.0,
            XAxisMaxNm = 100.0,
        };

        config.Normalize();

        Assert.Equal(0.1, config.XAxisMinNm);
        Assert.Equal(10000.0, config.XAxisMaxNm);
    }

    [Fact]
    public void Normalize_KeepsValidXAxisRange()
    {
        var config = new GraphFormattingConfig
        {
            XAxisMinNm = 1.0,
            XAxisMaxNm = 1000.0,
        };

        config.Normalize();

        Assert.Equal(1.0, config.XAxisMinNm);
        Assert.Equal(1000.0, config.XAxisMaxNm);
    }

    [Fact]
    public void Normalize_ResetsYAxisRangeOnNaNOrInversion()
    {
        var nan = new GraphFormattingConfig { YAxisMinPercent = double.NaN };
        var inverted = new GraphFormattingConfig { YAxisMinPercent = 50.0, YAxisMaxPercent = 10.0 };

        nan.Normalize();
        inverted.Normalize();

        Assert.Equal(0.0, nan.YAxisMinPercent);
        Assert.Equal(30.0, nan.YAxisMaxPercent);
        Assert.Equal(0.0, inverted.YAxisMinPercent);
        Assert.Equal(30.0, inverted.YAxisMaxPercent);
    }

    [Fact]
    public void Normalize_KeepsValidYAxisRange()
    {
        var config = new GraphFormattingConfig
        {
            YAxisMinPercent = 5.0,
            YAxisMaxPercent = 25.0,
        };

        config.Normalize();

        Assert.Equal(5.0, config.YAxisMinPercent);
        Assert.Equal(25.0, config.YAxisMaxPercent);
    }

    [Fact]
    public void Normalize_ClampsNegativeDefaultRunIndexToZero()
    {
        var config = new GraphFormattingConfig { DefaultRunIndex = -3 };

        config.Normalize();

        Assert.Equal(0, config.DefaultRunIndex);
    }

    [Fact]
    public void Normalize_StillRunsBaseNormalization()
    {
        // Sanity-check that base.Normalize() is invoked: a non-positive
        // FontSize on the base should snap back to its default after we
        // call our subclass override.
        var config = new GraphFormattingConfig { FontSize = 0 };

        config.Normalize();

        Assert.Equal(GraphFormattingConfigBase.DefaultFontSize, config.FontSize);
    }
}
