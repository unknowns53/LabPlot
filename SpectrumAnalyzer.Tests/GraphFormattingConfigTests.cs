using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class GraphFormattingConfigTests
{
    [Fact]
    public void Defaults_AreSetForLambdaMaxAndCloudPoint()
    {
        var config = new GraphFormattingConfig();

        Assert.False(config.ShowLambdaMaxMarkers);
        Assert.Equal(0.05, config.LambdaMaxMinAbsorbance);
        Assert.Equal(3, config.LambdaMaxCount);

        Assert.False(config.ShowCloudPointMarkers);
        Assert.Null(config.CloudPointMethod);
        Assert.Equal(50.0, config.CloudPointThresholdPercent);
    }

    [Fact]
    public void Normalize_ResetsOutOfRangeLambdaMaxFields()
    {
        var config = new GraphFormattingConfig
        {
            LambdaMaxMinAbsorbance = double.NaN,
            LambdaMaxCount = -5,
        };

        config.Normalize();

        Assert.Equal(0.05, config.LambdaMaxMinAbsorbance);
        Assert.Equal(3, config.LambdaMaxCount);
    }

    [Fact]
    public void Normalize_ResetsOutOfRangeCloudPointThreshold()
    {
        var config = new GraphFormattingConfig
        {
            CloudPointThresholdPercent = 200.0,
        };

        config.Normalize();

        Assert.Equal(50.0, config.CloudPointThresholdPercent);
    }

    [Fact]
    public void Normalize_KeepsValidCloudPointMethodTags()
    {
        var midpoint = new GraphFormattingConfig { CloudPointMethod = "Midpoint" };
        var derivative = new GraphFormattingConfig { CloudPointMethod = "FirstDerivativePeak" };
        var legacy = new GraphFormattingConfig { CloudPointMethod = "Derivative" };
        var unknown = new GraphFormattingConfig { CloudPointMethod = "Bogus" };

        midpoint.Normalize();
        derivative.Normalize();
        legacy.Normalize();
        unknown.Normalize();

        Assert.Equal("Midpoint", midpoint.CloudPointMethod);
        Assert.Equal("FirstDerivativePeak", derivative.CloudPointMethod);
        Assert.Equal("FirstDerivativePeak", legacy.CloudPointMethod);
        Assert.Null(unknown.CloudPointMethod);
    }

    [Fact]
    public void Normalize_PreservesValidLambdaMaxValues()
    {
        var config = new GraphFormattingConfig
        {
            ShowLambdaMaxMarkers = true,
            LambdaMaxMinAbsorbance = 0.2,
            LambdaMaxCount = 10,
            ShowCloudPointMarkers = true,
            CloudPointMethod = "Midpoint",
            CloudPointThresholdPercent = 80.0,
        };

        config.Normalize();

        Assert.True(config.ShowLambdaMaxMarkers);
        Assert.Equal(0.2, config.LambdaMaxMinAbsorbance);
        Assert.Equal(10, config.LambdaMaxCount);
        Assert.True(config.ShowCloudPointMarkers);
        Assert.Equal("Midpoint", config.CloudPointMethod);
        Assert.Equal(80.0, config.CloudPointThresholdPercent);
    }
}
