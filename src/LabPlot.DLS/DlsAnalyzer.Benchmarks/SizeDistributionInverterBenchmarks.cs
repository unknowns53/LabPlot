using BenchmarkDotNet.Attributes;
using DlsAnalyzer.Core;

namespace DlsAnalyzer.Benchmarks;

/// <summary>
/// Baseline benchmarks for <see cref="SizeDistributionInverter"/> — the
/// Tikhonov-regularised NNLS inverter used to recover a particle size
/// distribution from g₂-1 (τ). The Phase 1 explore agent flagged this
/// path as a likely allocation hotspot (matrix K + difference operator
/// L are rebuilt every invocation; the auto-α sweep solves the NNLS
/// system once per candidate). The benchmark establishes a v1.3.x
/// baseline; no implementation changes ship in this PR — those will be
/// scoped to a follow-up if the numbers warrant it.
///
/// Two strategies are timed: a single fixed-α solve (cheap, dominated
/// by one NNLS) and the default 16-point auto-α sweep (16× NNLS plus
/// L-curve corner picking).
/// </summary>
[MemoryDiagnoser]
public class SizeDistributionInverterBenchmarks
{
    private CorrelationFunction _correlation = null!;
    private SizeDistributionInverterOptions _autoAlphaOptions = null!;
    private SizeDistributionInverterOptions _fixedAlphaOptions = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _correlation = BuildSyntheticCorrelation();
        _autoAlphaOptions = new SizeDistributionInverterOptions
        {
            // Defaults from the production Options record (16 candidates,
            // 1e-4 .. 1.0, 60 bins). Listed explicitly so the bench keeps
            // tracking the same configuration even if the defaults shift.
            BinCount = 60,
            MinDiameterNm = 0.4,
            MaxDiameterNm = 10_000.0,
            AutoAlphaCandidateCount = 16,
            AutoAlphaMin = 1e-4,
            AutoAlphaMax = 1.0,
        };
        _fixedAlphaOptions = _autoAlphaOptions with
        {
            // Single-solve variant. α = 1e-2 sits roughly in the middle of
            // the auto-sweep range; the exact value is unimportant, the
            // point is to time one NNLS solve in isolation from the L-curve
            // machinery.
            RegularizationAlpha = 1e-2,
        };
    }

    /// <summary>
    /// Single NNLS solve with a fixed α — the cheap branch.
    /// </summary>
    [Benchmark(Baseline = true)]
    public SizeDistributionInversionOutcome InvertFixedAlpha()
    {
        return SizeDistributionInverter.Invert(
            _correlation,
            temperatureCelsius: 25.0,
            viscosityMpas: 0.89,
            refractiveIndex: 1.33,
            wavelengthNm: 633.0,
            scatteringAngleDegrees: 173.0,
            options: _fixedAlphaOptions);
    }

    /// <summary>
    /// Default 16-point auto-α sweep — the production UI path. 16× NNLS
    /// solves + L-curve corner picking; dominates allocation budget.
    /// </summary>
    [Benchmark]
    public SizeDistributionInversionOutcome InvertAutoAlpha()
    {
        return SizeDistributionInverter.Invert(
            _correlation,
            temperatureCelsius: 25.0,
            viscosityMpas: 0.89,
            refractiveIndex: 1.33,
            wavelengthNm: 633.0,
            scatteringAngleDegrees: 173.0,
            options: _autoAlphaOptions);
    }

    /// <summary>
    /// Realistic synthetic g₂-1 (τ): single-peak log-Gaussian centred at
    /// 100 nm in water, β = 0.9, ~150 τ samples on a log grid 0.5 μs ..
    /// 5 s. The peak position and width are typical for a globular protein
    /// sample; no personal data.
    /// </summary>
    private static CorrelationFunction BuildSyntheticCorrelation()
    {
        const int n = 150;
        var taus = LogSpace(0.5, 5_000_000.0, n);
        const double gamma = 0.005; // 1/μs, corresponds to ~100 nm in water at 25°C / 173°
        const double beta = 0.9;
        var values = new double[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = beta * Math.Exp(-2 * gamma * taus[i]);
        }
        return new CorrelationFunction
        {
            TimesMicroseconds = taus,
            Runs = new IReadOnlyList<double>[] { values },
            ActiveRunIndex = 0,
        };
    }

    private static double[] LogSpace(double min, double max, int count)
    {
        var logMin = Math.Log10(min);
        var logMax = Math.Log10(max);
        var step = (logMax - logMin) / (count - 1);
        var result = new double[count];
        for (int i = 0; i < count; i++) result[i] = Math.Pow(10, logMin + i * step);
        return result;
    }
}
