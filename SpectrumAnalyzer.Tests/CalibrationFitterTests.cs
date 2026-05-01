using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class CalibrationFitterTests
{
    [Fact]
    public void Fit_ForceOrigin_PerfectLine_RecoversSlope()
    {
        // A = 50,000 * c, l = 1 cm → slope = 50,000, ε = 50,000 M⁻¹·cm⁻¹
        var inputs = MakeInputs(new[]
        {
            (1e-6, 0.05),
            (2e-6, 0.10),
            (5e-6, 0.25),
            (10e-6, 0.50),
        });

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.True(result.HasFit);
        Assert.Equal(50_000.0, result.Slope, precision: 3);
        Assert.Equal(0.0, result.Intercept, precision: 12);
        Assert.Equal(50_000.0, result.EpsilonPerCmPerMolar, precision: 3);
        Assert.Equal(4, result.N);
        Assert.Equal(1.0, result.RSquared, precision: 9);
    }

    [Fact]
    public void Fit_WithIntercept_PerfectLine_RecoversSlopeAndIntercept()
    {
        // A = 30,000 * c + 0.05
        var inputs = MakeInputs(new[]
        {
            (1e-6, 0.08),
            (2e-6, 0.11),
            (5e-6, 0.20),
            (10e-6, 0.35),
        });

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.WithIntercept,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.True(result.HasFit);
        Assert.Equal(30_000.0, result.Slope, precision: 3);
        Assert.Equal(0.05, result.Intercept, precision: 9);
        Assert.Equal(1.0, result.RSquared, precision: 9);
    }

    [Fact]
    public void Fit_PathLength_AffectsEpsilon()
    {
        // slope = 60,000; l = 0.5 cm → ε = 120,000 M⁻¹·cm⁻¹
        var inputs = MakeInputs(new[]
        {
            (1e-6, 0.06),
            (2e-6, 0.12),
            (5e-6, 0.30),
        });

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 0.5);

        Assert.Equal(60_000.0, result.Slope, precision: 3);
        Assert.Equal(120_000.0, result.EpsilonPerCmPerMolar, precision: 3);
    }

    [Fact]
    public void Fit_FewerThanTwoPoints_ReturnsEmpty()
    {
        var inputs = MakeInputs(new[]
        {
            (1e-6, 0.05),
        });

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.False(result.HasFit);
        Assert.Equal(0, result.N);
        Assert.True(double.IsNaN(result.Slope));
    }

    [Fact]
    public void Fit_AllExcluded_ReturnsEmpty()
    {
        var inputs = MakeInputs(new[]
        {
            (1e-6, 0.05),
            (2e-6, 0.10),
        }, excludeAll: true);

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.False(result.HasFit);
        Assert.Equal(0, result.N);
        Assert.Equal(2, result.Points.Count); // rows are still kept for inspection
        Assert.All(result.Points, p => Assert.True(p.IsExcluded));
    }

    [Fact]
    public void Fit_ExcludedPoint_NotInRegression_ButPredictedFromFit()
    {
        // First three points sit perfectly on slope = 100,000. The fourth is
        // an outlier and is excluded — its predicted value should still
        // come from the (clean) fit.
        var inputs = new List<CalibrationFitInput>
        {
            new() { DatasetKey = "a", DisplayName = "a", ConcentrationMolar = 1e-6, Signal = 0.10 },
            new() { DatasetKey = "b", DisplayName = "b", ConcentrationMolar = 2e-6, Signal = 0.20 },
            new() { DatasetKey = "c", DisplayName = "c", ConcentrationMolar = 4e-6, Signal = 0.40 },
            new() { DatasetKey = "d", DisplayName = "d", ConcentrationMolar = 5e-6, Signal = 5.00,
                IsExcluded = true },
        };

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.True(result.HasFit);
        Assert.Equal(3, result.N);
        Assert.Equal(100_000.0, result.Slope, precision: 3);

        var outlier = result.Points.Single(p => p.DatasetKey == "d");
        Assert.True(outlier.IsExcluded);
        Assert.True(outlier.HasSignal);
        Assert.Equal(0.5, outlier.Predicted, precision: 9);
        Assert.Equal(4.5, outlier.Residual, precision: 9);
    }

    [Fact]
    public void Fit_NaNSignal_RowSkippedFromFit_ButPresentInPoints()
    {
        var inputs = new List<CalibrationFitInput>
        {
            new() { DatasetKey = "a", DisplayName = "a", ConcentrationMolar = 1e-6, Signal = 0.10 },
            new() { DatasetKey = "b", DisplayName = "b", ConcentrationMolar = 2e-6, Signal = 0.20 },
            new() { DatasetKey = "c", DisplayName = "c", ConcentrationMolar = 3e-6, Signal = double.NaN },
        };

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.Equal(2, result.N);
        Assert.True(result.HasFit);
        var bad = result.Points.Single(p => p.DatasetKey == "c");
        Assert.False(bad.HasSignal);
    }

    [Fact]
    public void Fit_NullConcentration_RowSkippedFromFit()
    {
        var inputs = new List<CalibrationFitInput>
        {
            new() { DatasetKey = "a", DisplayName = "a", ConcentrationMolar = 1e-6, Signal = 0.10 },
            new() { DatasetKey = "b", DisplayName = "b", ConcentrationMolar = 2e-6, Signal = 0.20 },
            new() { DatasetKey = "c", DisplayName = "c", ConcentrationMolar = null, Signal = 0.50 },
        };

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.Equal(2, result.N);
        var unset = result.Points.Single(p => p.DatasetKey == "c");
        Assert.False(unset.HasSignal);
        Assert.True(double.IsNaN(unset.ConcentrationMolar));
    }

    [Fact]
    public void Fit_NoisySamples_RSquaredBetweenZeroAndOne()
    {
        // slope ~= 50,000 with 5 % noise on each absorbance
        var rng = new Random(42);
        var inputs = new List<CalibrationFitInput>();
        for (var i = 1; i <= 8; i++)
        {
            var c = i * 1e-6;
            var pure = 50_000 * c;
            var noisy = pure * (1.0 + (rng.NextDouble() - 0.5) * 0.1);
            inputs.Add(new CalibrationFitInput
            {
                DatasetKey = $"s{i}",
                DisplayName = $"s{i}",
                ConcentrationMolar = c,
                Signal = noisy,
            });
        }

        var result = CalibrationFitter.Fit(
            inputs,
            CalibrationFitMode.ForceOrigin,
            CalibrationQuantificationMode.SingleWavelength,
            pathLengthCm: 1.0);

        Assert.True(result.HasFit);
        Assert.True(result.RSquared > 0.9 && result.RSquared <= 1.0,
            $"R² out of expected range: {result.RSquared}");
    }

    private static IReadOnlyList<CalibrationFitInput> MakeInputs(
        IEnumerable<(double Concentration, double Signal)> rows,
        bool excludeAll = false)
    {
        var i = 0;
        var list = new List<CalibrationFitInput>();
        foreach (var (c, s) in rows)
        {
            list.Add(new CalibrationFitInput
            {
                DatasetKey = $"s{i++}",
                DisplayName = $"sample {i}",
                ConcentrationMolar = c,
                Signal = s,
                IsExcluded = excludeAll,
            });
        }

        return list;
    }
}
