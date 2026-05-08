using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

/// <summary>
/// Synthetic-data round-trips for the Boltzmann ramp fit. Every test
/// generates points from a known Boltzmann sigmoid (optionally with
/// gaussian noise) and asserts the recovered T_c / w / plateaus stay
/// within reasonable bounds.
/// </summary>
public sealed class TemperatureRampAnalyzerTests
{
    private static List<TemperatureRampPoint> Sample(
        double dLow, double dHigh, double tc, double w,
        double[] temperatures, double noiseSigma = 0, int seed = 12345)
    {
        var rng = new Random(seed);
        var points = new List<TemperatureRampPoint>(temperatures.Length);
        foreach (var t in temperatures)
        {
            var s = 1.0 / (1.0 + Math.Exp(-(t - tc) / w));
            var d = dLow + (dHigh - dLow) * s;
            if (noiseSigma > 0)
            {
                var u1 = 1.0 - rng.NextDouble();
                var u2 = 1.0 - rng.NextDouble();
                var n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                d += n * noiseSigma;
            }
            points.Add(new TemperatureRampPoint(t, d));
        }
        return points;
    }

    [Fact]
    public void RecoversParametersFromCleanPnipamLikeRamp()
    {
        var ts = new[] { 25.0, 27.0, 29.0, 30.0, 31.0, 32.0, 33.0, 35.0 };
        var points = Sample(dLow: 10, dHigh: 200, tc: 31.0, w: 0.8, ts);

        var outcome = TemperatureRampAnalyzer.Analyze(points);

        Assert.True(outcome.Success, outcome.FailureReason);
        var r = outcome.Result!;
        Assert.InRange(r.TransitionTemperatureCelsius, 30.9, 31.1);
        Assert.InRange(Math.Abs(r.TransitionWidthCelsius), 0.7, 0.9);
        Assert.InRange(r.LowPlateauNm, 9.5, 10.5);
        Assert.InRange(r.HighPlateauNm, 195, 205);
        Assert.True(r.RSquared > 0.99);
    }

    [Fact]
    public void TolerantToModerateGaussianNoise()
    {
        var ts = new[] { 25.0, 26.0, 27.0, 28.0, 29.0, 30.0, 30.5, 31.0, 31.5, 32.0, 33.0, 35.0 };
        var points = Sample(dLow: 10, dHigh: 200, tc: 31.0, w: 0.8, ts, noiseSigma: 5.0);

        var outcome = TemperatureRampAnalyzer.Analyze(points);

        Assert.True(outcome.Success, outcome.FailureReason);
        var r = outcome.Result!;
        Assert.InRange(r.TransitionTemperatureCelsius, 30.0, 32.0);
        Assert.InRange(Math.Abs(r.TransitionWidthCelsius), 0.4, 1.5);
        // Plateaus stay within a few sigma of the truth even with noise.
        Assert.InRange(r.LowPlateauNm, 0, 25);
        Assert.InRange(r.HighPlateauNm, 180, 220);
    }

    [Fact]
    public void HandlesInvertedRampLikeUcstPolymer()
    {
        // Hypothetical UCST-style polymer: collapses on cooling.
        // The Boltzmann model has a symmetry — (d_low, d_high, w) and
        // (d_high, d_low, -w) describe the same curve — so the fit may
        // legitimately settle on either parameterisation. Verify the
        // recovered curve through the Predict helper instead.
        var ts = new[] { 20.0, 22.0, 24.0, 26.0, 28.0, 30.0 };
        var points = Sample(dLow: 200, dHigh: 10, tc: 25.0, w: 0.5, ts);

        var outcome = TemperatureRampAnalyzer.Analyze(points);

        Assert.True(outcome.Success, outcome.FailureReason);
        var r = outcome.Result!;
        Assert.InRange(r.TransitionTemperatureCelsius, 24.5, 25.5);
        // Cold side should reproduce the large diameter, hot side the small.
        Assert.InRange(TemperatureRampAnalyzer.Predict(20.0, r), 195, 205);
        Assert.InRange(TemperatureRampAnalyzer.Predict(30.0, r), 5, 15);
    }

    [Fact]
    public void RejectsTooFewPoints()
    {
        var points = new List<TemperatureRampPoint>
        {
            new(25.0, 10.0),
            new(35.0, 200.0),
        };

        var outcome = TemperatureRampAnalyzer.Analyze(points);

        Assert.False(outcome.Success);
        Assert.Contains("不足", outcome.FailureReason);
    }

    [Fact]
    public void RejectsFlatTemperatureRange()
    {
        var points = new List<TemperatureRampPoint>
        {
            new(25.0, 10.0),
            new(25.0, 10.5),
            new(25.0, 9.7),
            new(25.0, 10.2),
            new(25.0, 10.1),
        };

        var outcome = TemperatureRampAnalyzer.Analyze(points);

        Assert.False(outcome.Success);
        Assert.Contains("温度範囲", outcome.FailureReason);
    }

    [Fact]
    public void DropsNonFiniteAndZeroDiameterPoints()
    {
        var points = new List<TemperatureRampPoint>
        {
            new(25.0, 10.0),
            new(27.0, double.NaN),     // dropped
            new(29.0, 12.0),
            new(31.0, 0.0),            // dropped (non-positive)
            new(33.0, 150.0),
            new(35.0, 200.0),
            new(double.NaN, 100.0),    // dropped
        };

        var outcome = TemperatureRampAnalyzer.Analyze(points);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.Equal(4, outcome.Result!.PointCount);
    }

    [Fact]
    public void PredictReproducesSampledValues()
    {
        var ts = new[] { 25.0, 28.0, 31.0, 32.0, 35.0 };
        var points = Sample(dLow: 10, dHigh: 200, tc: 31.0, w: 0.8, ts);

        var outcome = TemperatureRampAnalyzer.Analyze(points);
        Assert.True(outcome.Success);

        // The fit should reproduce its own input within the residual scale.
        for (int i = 0; i < ts.Length; i++)
        {
            var predicted = TemperatureRampAnalyzer.Predict(ts[i], outcome.Result!);
            Assert.InRange(predicted, points[i].DiameterNm - 1.0, points[i].DiameterNm + 1.0);
        }
    }
}
