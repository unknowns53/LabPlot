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
        var second = new GraphFormattingConfig { CloudPointMethod = "SecondDerivativeExtremum" };
        var secondLegacy = new GraphFormattingConfig { CloudPointMethod = "SecondDerivative" };
        var unknown = new GraphFormattingConfig { CloudPointMethod = "Bogus" };

        midpoint.Normalize();
        derivative.Normalize();
        legacy.Normalize();
        second.Normalize();
        secondLegacy.Normalize();
        unknown.Normalize();

        Assert.Equal("Midpoint", midpoint.CloudPointMethod);
        Assert.Equal("FirstDerivativePeak", derivative.CloudPointMethod);
        Assert.Equal("FirstDerivativePeak", legacy.CloudPointMethod);
        Assert.Equal("SecondDerivativeExtremum", second.CloudPointMethod);
        Assert.Equal("SecondDerivativeExtremum", secondLegacy.CloudPointMethod);
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

    [Fact]
    public void Normalize_NullCalibration_StaysNull()
    {
        var config = new GraphFormattingConfig();
        config.Normalize();
        Assert.Null(config.Calibration);
    }

    [Fact]
    public void Normalize_CalibrationConfig_ResetsInvalidScalarFields()
    {
        var config = new GraphFormattingConfig
        {
            Calibration = new CalibrationCurveConfig
            {
                WavelengthNm = double.NaN,
                PathLengthCm = -1,
                MolarMass = -100,
                IntegrationRegionLabel = "  ",
            },
        };

        config.Normalize();

        Assert.NotNull(config.Calibration);
        Assert.Equal(280.0, config.Calibration!.WavelengthNm);
        Assert.Equal(1.0, config.Calibration.PathLengthCm);
        Assert.Null(config.Calibration.MolarMass);
        Assert.Null(config.Calibration.IntegrationRegionLabel);
    }

    [Fact]
    public void Normalize_CalibrationSamples_RemoveDuplicatesAndEmpty()
    {
        var config = new GraphFormattingConfig
        {
            Calibration = new CalibrationCurveConfig
            {
                Samples = new List<CalibrationSample>
                {
                    new() { DatasetKey = "a", ConcentrationInUnit = 1.0 },
                    new() { DatasetKey = "a", ConcentrationInUnit = 99.0 },
                    new() { DatasetKey = "  ", ConcentrationInUnit = 5.0 },
                    new() { DatasetKey = "b", ConcentrationInUnit = double.NaN },
                },
            },
        };

        config.Normalize();

        Assert.Equal(2, config.Calibration!.Samples.Count);
        Assert.Equal("a", config.Calibration.Samples[0].DatasetKey);
        Assert.Equal(1.0, config.Calibration.Samples[0].ConcentrationInUnit);
        Assert.Equal("b", config.Calibration.Samples[1].DatasetKey);
        Assert.Null(config.Calibration.Samples[1].ConcentrationInUnit);
    }
}
