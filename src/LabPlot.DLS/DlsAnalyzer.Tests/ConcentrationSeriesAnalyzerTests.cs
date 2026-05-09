using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

/// <summary>
/// Synthetic-data round-trips for the concentration-series fit. Every
/// test generates points from a known D(c) = D₀·(1 + k_D·c) recipe
/// (optionally with gaussian noise) and asserts the recovered D₀ /
/// k_D / d_h(c=0) stay within reasonable bounds.
/// </summary>
public sealed class ConcentrationSeriesAnalyzerTests
{
    private const double WaterViscosity25C = 0.890;
    private const double Reference25C = 25.0;

    /// <summary>D₀ that matches d_h = 10 nm at 25 °C in water (m²/s).</summary>
    private const double D0For10nmAt25C = 4.910e-11;

    private static List<ConcentrationSeriesPoint> Sample(
        double d0,
        double kDmlPerGram,
        double[] concentrationsMgPerMl,
        double noiseSigma = 0,
        int seed = 42)
    {
        var rng = new Random(seed);
        var points = new List<ConcentrationSeriesPoint>(concentrationsMgPerMl.Length);
        foreach (var c in concentrationsMgPerMl)
        {
            var cGPerMl = c * 1e-3;
            var d = d0 * (1.0 + kDmlPerGram * cGPerMl);
            if (noiseSigma > 0)
            {
                var u1 = 1.0 - rng.NextDouble();
                var u2 = 1.0 - rng.NextDouble();
                var n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                d += n * noiseSigma * d0;
            }
            points.Add(new ConcentrationSeriesPoint(c, d));
        }
        return points;
    }

    [Fact]
    public void RecoversNegativeKDFromCleanPnipamLikeSeries()
    {
        var cs = new[] { 0.5, 1.0, 2.0, 4.0, 6.0, 8.0, 10.0 };
        var points = Sample(D0For10nmAt25C, kDmlPerGram: -25.0, cs);

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.True(outcome.Success, outcome.FailureReason);
        var r = outcome.Result!;
        Assert.InRange(r.D0M2PerSecond, D0For10nmAt25C * 0.999, D0For10nmAt25C * 1.001);
        Assert.InRange(r.KDmlPerGram, -25.5, -24.5);
        Assert.InRange(r.HydrodynamicDiameterAtZeroConcentrationNm, 9.95, 10.05);
        Assert.True(r.RSquared > 0.999);
        Assert.Equal(7, r.PointCount);
    }

    [Fact]
    public void RecoversPositiveKDForRepulsiveSystem()
    {
        // BSA-like behaviour in a good solvent: positive k_D ≈ 12 mL/g.
        var cs = new[] { 1.0, 2.0, 4.0, 6.0, 8.0, 10.0 };
        var points = Sample(D0For10nmAt25C, kDmlPerGram: 12.0, cs);

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.InRange(outcome.Result!.KDmlPerGram, 11.5, 12.5);
        Assert.InRange(outcome.Result.HydrodynamicDiameterAtZeroConcentrationNm, 9.9, 10.1);
    }

    [Fact]
    public void TolerantToModerateGaussianNoise()
    {
        var cs = new[] { 0.5, 1.0, 2.0, 4.0, 6.0, 8.0, 10.0 };
        // 2% relative noise on D — typical Zetasizer reproducibility.
        var points = Sample(D0For10nmAt25C, kDmlPerGram: -25.0, cs, noiseSigma: 0.02, seed: 7);

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.True(outcome.Success, outcome.FailureReason);
        var r = outcome.Result!;
        Assert.InRange(r.KDmlPerGram, -35.0, -15.0);
        Assert.InRange(r.HydrodynamicDiameterAtZeroConcentrationNm, 9.0, 11.5);
        Assert.True(r.SlopeStandardError > 0);
        Assert.True(r.D0StandardErrorM2PerSecond > 0);
        Assert.True(r.KDStandardErrorMlPerGram > 0);
    }

    [Fact]
    public void RejectsTooFewPoints()
    {
        var points = new List<ConcentrationSeriesPoint>
        {
            new(1.0, 5.0e-11),
            new(5.0, 4.5e-11),
        };

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.False(outcome.Success);
        Assert.Contains("不足", outcome.FailureReason);
    }

    [Fact]
    public void RejectsFlatConcentrationRange()
    {
        var points = new List<ConcentrationSeriesPoint>
        {
            new(2.0, 5.0e-11),
            new(2.0, 4.9e-11),
            new(2.0, 5.1e-11),
            new(2.0, 5.0e-11),
        };

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.False(outcome.Success);
        Assert.Contains("濃度範囲", outcome.FailureReason);
    }

    [Fact]
    public void RejectsNonPhysicalIntercept()
    {
        // Pathological pattern: D rises so steeply with c that the OLS
        // line extrapolated back to c = 0 crosses zero. Real measurements
        // never look like this; the analyzer should refuse to report an
        // unphysical D₀ rather than push a negative diameter into the UI.
        var points = new List<ConcentrationSeriesPoint>
        {
            new(5.0, 1.0e-12),
            new(7.0, 5.0e-12),
            new(9.0, 9.0e-12),
            new(11.0, 1.3e-11),
        };

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.False(outcome.Success);
        Assert.Contains("D₀", outcome.FailureReason);
    }

    [Fact]
    public void DropsNonFiniteAndNonPositiveDPoints()
    {
        var points = new List<ConcentrationSeriesPoint>
        {
            new(0.5, D0For10nmAt25C),
            new(1.0, double.NaN),
            new(2.0, 4.5e-11),
            new(4.0, 0.0),
            new(6.0, 4.0e-11),
            new(8.0, 3.5e-11),
            new(double.NaN, 5.0e-11),
        };

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.Equal(4, outcome.Result!.PointCount);
    }

    [Fact]
    public void PredictReproducesSampledValues()
    {
        var cs = new[] { 0.5, 1.0, 2.0, 4.0, 6.0, 8.0 };
        var points = Sample(D0For10nmAt25C, kDmlPerGram: -25.0, cs);

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, Reference25C, WaterViscosity25C);
        Assert.True(outcome.Success);

        for (int i = 0; i < cs.Length; i++)
        {
            var predicted = ConcentrationSeriesAnalyzer.Predict(cs[i], outcome.Result!);
            Assert.InRange(predicted, points[i].DiffusionCoefficientM2PerSecond * 0.999,
                                       points[i].DiffusionCoefficientM2PerSecond * 1.001);
        }
    }

    [Fact]
    public void ReferenceConditionsRoundTripIntoResult()
    {
        var cs = new[] { 1.0, 2.0, 4.0, 6.0, 8.0 };
        var points = Sample(D0For10nmAt25C, kDmlPerGram: -10.0, cs);

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, 30.5, 0.798);

        Assert.True(outcome.Success);
        Assert.Equal(30.5, outcome.Result!.ReferenceTemperatureCelsius);
        Assert.Equal(0.798, outcome.Result.ReferenceViscosityMpas);
    }
}
