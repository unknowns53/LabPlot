using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

public class CumulantAnalyzerTests
{
    // Synthesise a noiseless g₂(τ)-1 = β·exp(-2Γτ + μ₂τ²) curve so the
    // fit should recover the input parameters to high precision.
    // β = 1 to keep the math clean (intercept a₀ = 0).
    private static CorrelationFunction SyntheticDecay(
        double gammaPerMicrosecond,
        double mu2PerMicrosecondSquared,
        double tauStart = 1.0,
        double tauEnd = 1000.0,
        int pointCount = 64)
    {
        var times = new double[pointCount];
        var values = new double[pointCount];
        // Log-spaced τ to mimic Zetasizer's correlator layout.
        var logStart = Math.Log10(tauStart);
        var logEnd = Math.Log10(tauEnd);
        for (int i = 0; i < pointCount; i++)
        {
            var t = i / (double)(pointCount - 1);
            var tau = Math.Pow(10, logStart + t * (logEnd - logStart));
            times[i] = tau;
            // ln(g₁) = -Γτ + (μ₂/2)τ² so g₂-1 = exp(-2Γτ + μ₂τ²).
            var lnG2 = -2.0 * gammaPerMicrosecond * tau
                + mu2PerMicrosecondSquared * tau * tau;
            values[i] = Math.Exp(lnG2);
        }
        return new CorrelationFunction
        {
            TimesMicroseconds = times,
            Runs = new[] { (IReadOnlyList<double>)values },
            ActiveRunIndex = 0,
        };
    }

    [Fact]
    public void Analyze_SyntheticDecay_RecoversGammaAndMu2()
    {
        const double gamma = 0.005;        // μs⁻¹
        const double mu2 = 5e-7;           // μs⁻²
        var corr = SyntheticDecay(gamma, mu2);

        var outcome = CumulantAnalyzer.Analyze(corr);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.NotNull(outcome.Result);
        Assert.Equal(gamma, outcome.Result!.FirstCumulantPerMicrosecond, precision: 4);
        Assert.Equal(mu2, outcome.Result.SecondCumulantPerMicrosecondSquared, precision: 8);
        Assert.Equal(mu2 / (gamma * gamma), outcome.Result.PolydispersityIndex, precision: 4);
        Assert.True(outcome.Result.RSquared > 0.999);
    }

    [Fact]
    public void Analyze_NullCorrelation_Fails()
    {
        var outcome = CumulantAnalyzer.Analyze(null);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Result);
        Assert.Equal("自己相関データがありません", outcome.FailureReason);
    }

    [Fact]
    public void Analyze_EmptyRuns_Fails()
    {
        var corr = new CorrelationFunction
        {
            TimesMicroseconds = Array.Empty<double>(),
            Runs = new[] { (IReadOnlyList<double>)Array.Empty<double>() },
            ActiveRunIndex = 0,
        };

        var outcome = CumulantAnalyzer.Analyze(corr);

        Assert.False(outcome.Success);
        Assert.Equal("自己相関データがありません", outcome.FailureReason);
    }

    [Fact]
    public void Analyze_TooFewPoints_Fails()
    {
        // Three points = below the quadratic minimum of 4.
        var corr = new CorrelationFunction
        {
            TimesMicroseconds = new[] { 1.0, 2.0, 3.0 },
            Runs = new[] { (IReadOnlyList<double>)new[] { 0.9, 0.8, 0.7 } },
            ActiveRunIndex = 0,
        };

        var outcome = CumulantAnalyzer.Analyze(corr);

        Assert.False(outcome.Success);
        Assert.Contains("不足", outcome.FailureReason);
    }

    [Fact]
    public void Analyze_AutoThreshold_DropsNoiseTail()
    {
        // Decaying signal followed by a noisy negative-mean baseline.
        // Auto-threshold should keep only the first 6 points.
        var times = new[] { 1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 64.0, 128.0, 256.0, 512.0 };
        var values = new[] { 0.9, 0.8, 0.65, 0.45, 0.25, 0.12, 0.03, -0.01, 0.005, -0.002 };

        var corr = new CorrelationFunction
        {
            TimesMicroseconds = times,
            Runs = new[] { (IReadOnlyList<double>)values },
            ActiveRunIndex = 0,
        };

        var outcome = CumulantAnalyzer.Analyze(corr);

        Assert.True(outcome.Success, outcome.FailureReason);
        // First 6 points are >= 0.1; auto-threshold should keep all of them.
        Assert.Equal(6, outcome.Result!.PointCount);
        Assert.Equal(1.0, outcome.Result.AppliedRangeMinMicroseconds);
        Assert.Equal(32.0, outcome.Result.AppliedRangeMaxMicroseconds);
    }

    [Fact]
    public void Analyze_ExplicitRange_OverridesAutoDetection()
    {
        // Even though tail points fall below the auto-threshold, an
        // explicit range must keep them so the user can probe the
        // late-decay region manually.
        var corr = SyntheticDecay(0.005, 5e-7, tauStart: 1, tauEnd: 1000, pointCount: 32);

        var outcome = CumulantAnalyzer.Analyze(corr,
            minMicroseconds: 100, maxMicroseconds: 800);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.True(outcome.Result!.AppliedRangeMinMicroseconds >= 100);
        Assert.True(outcome.Result.AppliedRangeMaxMicroseconds <= 800);
    }

    [Fact]
    public void Analyze_MixedRange_StopsAtFirstNoiseFallthrough()
    {
        // Manual lower bound + auto upper bound: τ ≥ 4 is pinned, but the
        // auto threshold should stop the window at the first sample that
        // drops below the threshold rather than re-including the post-dip
        // recovery at τ=256. The previous (auto-per-point) rule kept both
        // sides of the dip, which let baseline correlator noise leak into
        // the fit window.
        var times = new[] { 1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 64.0, 128.0, 256.0 };
        var values = new[] { 0.9, 0.8, 0.65, 0.45, 0.25, 0.12, 0.11, 0.05, 0.15 };

        var corr = new CorrelationFunction
        {
            TimesMicroseconds = times,
            Runs = new[] { (IReadOnlyList<double>)values },
            ActiveRunIndex = 0,
        };

        var outcome = CumulantAnalyzer.Analyze(corr, minMicroseconds: 4);

        Assert.True(outcome.Success, outcome.FailureReason);
        // τ ≥ 4 で有効 5 点 (4, 8, 16, 32, 64) を contiguous で採用、
        // 0.05 < threshold で stop、0.15 は拾わない。
        Assert.Equal(5, outcome.Result!.PointCount);
        Assert.Equal(4.0, outcome.Result.AppliedRangeMinMicroseconds);
        Assert.Equal(64.0, outcome.Result.AppliedRangeMaxMicroseconds);
    }

    [Fact]
    public void Analyze_WideTauRange_RemainsNumericallyStable()
    {
        // τ spans six decades (1 μs → 1e6 μs) with correspondingly tiny
        // decay constants. Raw normal equations accumulate sx⁴ ~ 1e24,
        // which would lose precision under Cramer's rule even though the
        // system is well-conditioned analytically. After centring and
        // scaling τ inside the analyzer, Γ and μ₂ should still come out
        // within a couple of percent of truth.
        const double gamma = 1e-6;     // μs⁻¹
        const double mu2 = 5e-13;       // μs⁻²
        var corr = SyntheticDecay(gamma, mu2, tauStart: 1.0, tauEnd: 1_000_000.0, pointCount: 128);

        var outcome = CumulantAnalyzer.Analyze(corr,
            minMicroseconds: 1, maxMicroseconds: 1_000_000);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.InRange(outcome.Result!.FirstCumulantPerMicrosecond,
            gamma * 0.98, gamma * 1.02);
        Assert.InRange(outcome.Result.SecondCumulantPerMicrosecondSquared,
            mu2 * 0.9, mu2 * 1.1);
        Assert.True(outcome.Result.RSquared > 0.999);
    }

    [Fact]
    public void Analyze_NegativeGrowth_Fails()
    {
        // Constant or growing g₂-1 cannot represent a real decay.
        // Construct rising data and confirm the unphysical-Γ guard fires.
        var times = new[] { 1.0, 2.0, 4.0, 8.0, 16.0, 32.0 };
        var values = new[] { 0.2, 0.3, 0.4, 0.5, 0.6, 0.7 };

        var corr = new CorrelationFunction
        {
            TimesMicroseconds = times,
            Runs = new[] { (IReadOnlyList<double>)values },
            ActiveRunIndex = 0,
        };

        var outcome = CumulantAnalyzer.Analyze(corr);

        Assert.False(outcome.Success);
        Assert.Contains("非物理的", outcome.FailureReason);
    }
}
