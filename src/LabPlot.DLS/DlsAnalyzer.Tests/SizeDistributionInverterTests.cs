using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

/// <summary>
/// Synthetic-data round-trips for the Tikhonov-regularised NNLS size
/// distribution inverter. Each test builds a g₂-1(τ) trace from a
/// known sum of single-exponential populations, runs the inverter,
/// and asserts that the recovered intensity-weighted distribution
/// peaks at the right diameter(s) within an acceptable bin tolerance.
/// </summary>
public sealed class SizeDistributionInverterTests
{
    // Standard Zetasizer-like optics / solvent assumed across tests.
    private const double TempCelsius = 25.0;
    private const double ViscosityMpas = 0.890;
    private const double RefractiveIndex = 1.330;
    private const double WavelengthNm = 633.0;
    private const double ScatteringAngleDeg = 173.0;
    private const double Beta = 0.92;

    /// <summary>
    /// Build a noiseless g₂-1(τ) trace from a sum of single-exponential
    /// populations weighted by intensity. The same physics that drives
    /// <see cref="StokesEinstein"/> is used here to convert each
    /// population's diameter into its first cumulant Γ.
    /// </summary>
    private static CorrelationFunction BuildCorrelation(
        (double DiameterNm, double IntensityWeight)[] populations,
        int sampleCount = 90,
        double tauMinMicroseconds = 0.5,
        double tauMaxMicroseconds = 1.0e6,
        double noiseSigma = 0,
        int seed = 4242)
    {
        var totalWeight = 0.0;
        foreach (var (_, w) in populations) totalWeight += w;
        var weights = new double[populations.Length];
        var gammas = new double[populations.Length];
        for (int k = 0; k < populations.Length; k++)
        {
            weights[k] = populations[k].IntensityWeight / totalWeight;
            gammas[k] = ExpectedGamma(populations[k].DiameterNm);
        }

        var times = new double[sampleCount];
        var values = new double[sampleCount];
        var logMin = Math.Log10(tauMinMicroseconds);
        var logMax = Math.Log10(tauMaxMicroseconds);
        var rng = new Random(seed);
        for (int i = 0; i < sampleCount; i++)
        {
            var tau = Math.Pow(10, logMin + (logMax - logMin) * i / (sampleCount - 1));
            double g1 = 0;
            for (int k = 0; k < populations.Length; k++)
                g1 += weights[k] * Math.Exp(-gammas[k] * tau);
            var g2m1 = Beta * g1 * g1;
            if (noiseSigma > 0)
            {
                var u1 = 1.0 - rng.NextDouble();
                var u2 = 1.0 - rng.NextDouble();
                var n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                g2m1 += n * noiseSigma;
            }
            times[i] = tau;
            values[i] = g2m1;
        }
        return new CorrelationFunction
        {
            TimesMicroseconds = times,
            Runs = new IReadOnlyList<double>[] { values },
            ActiveRunIndex = 0,
        };
    }

    private static double ExpectedGamma(double diameterNm)
    {
        const double kB = 1.380649e-23;
        var dMeter = diameterNm * 1e-9;
        var etaPa = ViscosityMpas * 1e-3;
        var tK = TempCelsius + 273.15;
        var d = kB * tK / (3.0 * Math.PI * etaPa * dMeter);
        var lambdaM = WavelengthNm * 1e-9;
        var thetaR = ScatteringAngleDeg * Math.PI / 180.0;
        var q = (4.0 * Math.PI * RefractiveIndex / lambdaM) * Math.Sin(thetaR / 2.0);
        return d * q * q * 1e-6; // μs⁻¹
    }

    private static int IndexOfPeak(IReadOnlyList<SizeDistributionInversionBin> bins,
                                    Func<SizeDistributionInversionBin, double> selector)
    {
        int best = 0;
        double bestVal = selector(bins[0]);
        for (int i = 1; i < bins.Count; i++)
        {
            var v = selector(bins[i]);
            if (v > bestVal) { best = i; bestVal = v; }
        }
        return best;
    }

    [Fact]
    public void RecoversMonomodalPeakAroundExpectedDiameter()
    {
        var corr = BuildCorrelation(new[] { (10.0, 1.0) });

        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg);

        Assert.True(outcome.Success, outcome.FailureReason);
        var bins = outcome.Result!.Bins;
        var peakIdx = IndexOfPeak(bins, b => b.IntensityWeight);
        var peakDiameter = bins[peakIdx].DiameterNm;
        Assert.InRange(peakDiameter, 7.0, 14.0);
        Assert.True(outcome.Result.RSquared > 0.99,
            $"Fit quality too low: R² = {outcome.Result.RSquared}");
    }

    [Fact]
    public void RecoversBimodalPeaksAroundExpectedDiameters()
    {
        // Two well-separated populations: 8 nm coil + 200 nm globule with
        // intensity weights matching the LCST demo recipe.
        var corr = BuildCorrelation(new[] { (8.0, 0.20), (200.0, 0.80) });

        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg);

        Assert.True(outcome.Success, outcome.FailureReason);
        var bins = outcome.Result!.Bins;
        // Find the bin with the highest intensity weight in each half of
        // the size axis. Both should sit close to the recipe diameters.
        int splitIndex = bins.Count / 2;
        int lowIdx = 0; double lowVal = -1;
        int highIdx = splitIndex; double highVal = -1;
        for (int i = 0; i < splitIndex; i++)
        {
            if (bins[i].IntensityWeight > lowVal) { lowVal = bins[i].IntensityWeight; lowIdx = i; }
        }
        for (int i = splitIndex; i < bins.Count; i++)
        {
            if (bins[i].IntensityWeight > highVal) { highVal = bins[i].IntensityWeight; highIdx = i; }
        }
        Assert.InRange(bins[lowIdx].DiameterNm, 4.0, 20.0);
        Assert.InRange(bins[highIdx].DiameterNm, 100.0, 400.0);
        // The 200 nm population should dominate the intensity-weighted
        // result by at least 2× given the 80 / 20 weighting plus the
        // size⁶ Rayleigh enhancement.
        Assert.True(highVal > 2 * lowVal,
            $"High-d peak ({highVal}) should dominate low-d ({lowVal})");
    }

    [Fact]
    public void TolerantToModerateGaussianNoise()
    {
        var corr = BuildCorrelation(new[] { (10.0, 1.0) }, noiseSigma: 0.005, seed: 11);

        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg);

        Assert.True(outcome.Success, outcome.FailureReason);
        var bins = outcome.Result!.Bins;
        var peakDiameter = bins[IndexOfPeak(bins, b => b.IntensityWeight)].DiameterNm;
        Assert.InRange(peakDiameter, 6.0, 16.0);
    }

    [Fact]
    public void NumberWeightedPeakIsBelowIntensityWeightedPeakForBimodal()
    {
        // Same 8 / 200 nm bimodal sample. Number-weighted distribution
        // re-emphasises the small population (because Number ∝ 1/d⁶ on
        // top of the intensity weights), so its peak should be on the
        // small-d side even when intensity is dominated by the globule.
        var corr = BuildCorrelation(new[] { (8.0, 0.20), (200.0, 0.80) });
        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg);

        Assert.True(outcome.Success, outcome.FailureReason);
        var bins = outcome.Result!.Bins;
        var intensityPeak = IndexOfPeak(bins, b => b.IntensityWeight);
        var numberPeak = IndexOfPeak(bins, b => b.NumberWeight);
        Assert.True(numberPeak < intensityPeak,
            $"Number peak ({numberPeak}, d={bins[numberPeak].DiameterNm}) " +
            $"should sit at smaller d than intensity peak ({intensityPeak}, d={bins[intensityPeak].DiameterNm})");
    }

    [Fact]
    public void ReportsMissingMetadataWithFieldList()
    {
        var corr = BuildCorrelation(new[] { (10.0, 1.0) });

        var outcome = SizeDistributionInverter.Invert(
            corr, temperatureCelsius: null, viscosityMpas: null,
            refractiveIndex: null, wavelengthNm: null, scatteringAngleDegrees: null);

        Assert.False(outcome.Success);
        Assert.Contains("温度", outcome.MissingFields);
        Assert.Contains("粘度", outcome.MissingFields);
        Assert.Contains("屈折率", outcome.MissingFields);
        Assert.Contains("波長", outcome.MissingFields);
        Assert.Contains("散乱角", outcome.MissingFields);
    }

    [Fact]
    public void RejectsTooFewSamples()
    {
        var corr = new CorrelationFunction
        {
            TimesMicroseconds = new[] { 1.0, 2.0, 3.0 },
            Runs = new IReadOnlyList<double>[] { new[] { 0.5, 0.4, 0.3 } },
            ActiveRunIndex = 0,
        };
        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg);

        Assert.False(outcome.Success);
        Assert.Contains("点数", outcome.FailureReason);
    }

    [Fact]
    public void HonoursManualRegularizationAlpha()
    {
        var corr = BuildCorrelation(new[] { (10.0, 1.0) });
        var opts = new SizeDistributionInverterOptions { RegularizationAlpha = 0.05 };

        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg, opts);

        Assert.True(outcome.Success, outcome.FailureReason);
        Assert.Equal(0.05, outcome.Result!.RegularizationAlpha);
    }

    [Fact]
    public void DistributionsSumToOneHundredPercent()
    {
        var corr = BuildCorrelation(new[] { (10.0, 1.0) });
        var outcome = SizeDistributionInverter.Invert(
            corr, TempCelsius, ViscosityMpas, RefractiveIndex, WavelengthNm, ScatteringAngleDeg);

        Assert.True(outcome.Success);
        var bins = outcome.Result!.Bins;
        double iSum = 0, nSum = 0, vSum = 0;
        foreach (var b in bins)
        {
            iSum += b.IntensityWeight;
            nSum += b.NumberWeight;
            vSum += b.VolumeWeight;
        }
        Assert.InRange(iSum, 99.99, 100.01);
        Assert.InRange(nSum, 99.99, 100.01);
        Assert.InRange(vSum, 99.99, 100.01);
    }
}
