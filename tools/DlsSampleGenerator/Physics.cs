namespace LabPlot.Tools.DlsSampleGenerator;

/// <summary>
/// Light-scattering and DLS physics helpers used by the demo data
/// generator. Equations match DlsAnalyzer.Core.StokesEinstein so the
/// generated xlsx, when fed back into LabPlot, recovers the diameter
/// that was originally requested (within a few percent owing to the
/// added noise and discrete tau grid).
/// </summary>
internal static class Physics
{
    public const double BoltzmannJoulePerKelvin = 1.380649e-23;

    public static double ScatteringVectorPerMeter(double refractiveIndex, double wavelengthNm, double scatteringAngleDeg)
    {
        var lambdaMeter = wavelengthNm * 1e-9;
        var thetaRad = scatteringAngleDeg * Math.PI / 180.0;
        return (4.0 * Math.PI * refractiveIndex / lambdaMeter) * Math.Sin(thetaRad / 2.0);
    }

    public static double DiffusionCoefficientM2PerSecond(double diameterNm, double temperatureCelsius, double viscosityMpas)
    {
        var diameterMeter = diameterNm * 1e-9;
        var etaPa = viscosityMpas * 1e-3;
        var tKelvin = temperatureCelsius + 273.15;
        return BoltzmannJoulePerKelvin * tKelvin / (3.0 * Math.PI * etaPa * diameterMeter);
    }

    /// <summary>Return the first cumulant in microsecond^-1 for the supplied diameter.</summary>
    public static double FirstCumulantPerMicrosecond(
        double diameterNm,
        double temperatureCelsius,
        double viscosityMpas,
        double refractiveIndex,
        double wavelengthNm,
        double scatteringAngleDegrees)
    {
        var d = DiffusionCoefficientM2PerSecond(diameterNm, temperatureCelsius, viscosityMpas);
        var q = ScatteringVectorPerMeter(refractiveIndex, wavelengthNm, scatteringAngleDegrees);
        return d * q * q * 1e-6;
    }

    /// <summary>Lognormal probability density with the median and shape sigma (in ln d).</summary>
    public static double LognormalPdf(double d, double medianNm, double sigma)
    {
        if (d <= 0 || sigma <= 0) return 0;
        var lnRatio = Math.Log(d / medianNm);
        return Math.Exp(-(lnRatio * lnRatio) / (2 * sigma * sigma)) / (d * sigma * Math.Sqrt(2 * Math.PI));
    }

    /// <summary>Generate count log-spaced values between min and max inclusive.</summary>
    public static double[] Logspace(double min, double max, int count)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        var values = new double[count];
        var logMin = Math.Log10(min);
        var logMax = Math.Log10(max);
        for (int i = 0; i < count; i++)
            values[i] = Math.Pow(10, logMin + (logMax - logMin) * i / (count - 1));
        return values;
    }

    /// <summary>Round to a fixed number of decimal places without ToString round-tripping.</summary>
    public static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
