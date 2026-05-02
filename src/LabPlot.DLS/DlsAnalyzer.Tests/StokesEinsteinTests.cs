using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

public class StokesEinsteinTests
{
    // Standard Zetasizer optics in water at 25°C: a known particle
    // diameter d_h gives a known Γ. These constants come from the
    // Stokes–Einstein math worked through in reverse.
    private const double WaterTemperatureC = 25.0;
    private const double WaterViscosityMpas = 0.89;     // 25°C, water
    private const double WaterRefractiveIndex = 1.331;  // 633 nm, 25°C
    private const double ZetasizerWavelengthNm = 633.0;
    private const double ZetasizerScatteringAngleDeg = 173.0;

    [Fact]
    public void Compute_HundredNmPolystyreneStandard_RecoversDiameter()
    {
        // Reverse-engineered Γ for a 100 nm particle under the standard
        // optics & water at 25 °C. The forward calculation should
        // recover ~100 nm to within sub-percent precision.
        const double diameterTarget = 100.0;
        var gammaPerMicrosecond = ComputeForwardGamma(diameterTarget);

        var outcome = StokesEinstein.Compute(
            gammaPerMicrosecond,
            temperatureCelsius: WaterTemperatureC,
            viscosityMpas: WaterViscosityMpas,
            refractiveIndex: WaterRefractiveIndex,
            wavelengthNm: ZetasizerWavelengthNm,
            scatteringAngleDegrees: ZetasizerScatteringAngleDeg);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.HydrodynamicDiameterNm);
        Assert.Equal(diameterTarget, outcome.HydrodynamicDiameterNm!.Value, precision: 3);
        Assert.NotNull(outcome.DiffusionCoefficientM2PerSecond);
        Assert.NotNull(outcome.ScatteringVectorPerMeter);
        Assert.Empty(outcome.MissingFields);
    }

    [Fact]
    public void Compute_MissingTemperature_ReportsMissingField()
    {
        var outcome = StokesEinstein.Compute(
            firstCumulantPerMicrosecond: 0.005,
            temperatureCelsius: null,
            viscosityMpas: WaterViscosityMpas,
            refractiveIndex: WaterRefractiveIndex,
            wavelengthNm: ZetasizerWavelengthNm,
            scatteringAngleDegrees: ZetasizerScatteringAngleDeg);

        Assert.False(outcome.Success);
        Assert.Null(outcome.HydrodynamicDiameterNm);
        Assert.Contains("温度", outcome.MissingFields);
    }

    [Fact]
    public void Compute_AllMetadataMissing_ReportsAllFields()
    {
        var outcome = StokesEinstein.Compute(
            firstCumulantPerMicrosecond: 0.005,
            temperatureCelsius: null,
            viscosityMpas: null,
            refractiveIndex: null,
            wavelengthNm: null,
            scatteringAngleDegrees: null);

        Assert.False(outcome.Success);
        Assert.Equal(5, outcome.MissingFields.Count);
        Assert.Contains("温度", outcome.MissingFields);
        Assert.Contains("粘度", outcome.MissingFields);
        Assert.Contains("屈折率", outcome.MissingFields);
        Assert.Contains("波長", outcome.MissingFields);
        Assert.Contains("散乱角", outcome.MissingFields);
    }

    [Fact]
    public void Compute_NegativeGamma_ReportsGammaMissing()
    {
        var outcome = StokesEinstein.Compute(
            firstCumulantPerMicrosecond: -0.001,
            temperatureCelsius: WaterTemperatureC,
            viscosityMpas: WaterViscosityMpas,
            refractiveIndex: WaterRefractiveIndex,
            wavelengthNm: ZetasizerWavelengthNm,
            scatteringAngleDegrees: ZetasizerScatteringAngleDeg);

        Assert.False(outcome.Success);
        Assert.Contains("Γ", outcome.MissingFields);
    }

    [Fact]
    public void Compute_BelowFreezingTemperature_ReportsTemperatureMissing()
    {
        // -300 °C → T(K) = -26.85, which Stokes-Einstein cannot honour.
        var outcome = StokesEinstein.Compute(
            firstCumulantPerMicrosecond: 0.005,
            temperatureCelsius: -300.0,
            viscosityMpas: WaterViscosityMpas,
            refractiveIndex: WaterRefractiveIndex,
            wavelengthNm: ZetasizerWavelengthNm,
            scatteringAngleDegrees: ZetasizerScatteringAngleDeg);

        Assert.False(outcome.Success);
        Assert.Contains("温度", outcome.MissingFields);
    }

    // Stokes–Einstein in the forward direction: given d_h, what Γ does
    // the standard optics produce? Used by the round-trip test above.
    // Mirrors the body of StokesEinstein.Compute so the test does not
    // tautologically depend on the implementation.
    private static double ComputeForwardGamma(double diameterNm)
    {
        const double kB = 1.380649e-23;
        var tKelvin = WaterTemperatureC + 273.15;
        var etaPaSecond = WaterViscosityMpas * 1e-3;
        var lambdaMeter = ZetasizerWavelengthNm * 1e-9;
        var thetaRadian = ZetasizerScatteringAngleDeg * Math.PI / 180.0;
        var diameterMeter = diameterNm * 1e-9;

        var diffusion = kB * tKelvin / (3.0 * Math.PI * etaPaSecond * diameterMeter);
        var q = (4.0 * Math.PI * WaterRefractiveIndex / lambdaMeter)
                * Math.Sin(thetaRadian / 2.0);
        var gammaPerSecond = diffusion * q * q;
        return gammaPerSecond / 1e6;
    }
}
