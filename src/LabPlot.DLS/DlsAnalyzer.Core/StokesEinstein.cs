namespace DlsAnalyzer.Core;

/// <summary>
/// Outcome of converting a first cumulant Γ into a hydrodynamic
/// diameter. The diameter is reported in nm to match the rest of the
/// DLS UI (Zetasizer's standard output unit).
/// </summary>
public sealed record HydrodynamicSizeOutcome
{
    public required bool Success { get; init; }
    public double? HydrodynamicDiameterNm { get; init; }
    public double? DiffusionCoefficientM2PerSecond { get; init; }
    public double? ScatteringVectorPerMeter { get; init; }
    /// <summary>
    /// Human-readable list of metadata fields the caller still needs to
    /// supply to make the calculation possible (e.g. "温度", "粘度").
    /// Empty when the calculation succeeded.
    /// </summary>
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();

    public static HydrodynamicSizeOutcome Ok(double diameterNm, double diffusion, double q)
        => new()
        {
            Success = true,
            HydrodynamicDiameterNm = diameterNm,
            DiffusionCoefficientM2PerSecond = diffusion,
            ScatteringVectorPerMeter = q,
        };

    public static HydrodynamicSizeOutcome MissingMetadata(IReadOnlyList<string> missing)
        => new() { Success = false, MissingFields = missing };
}

/// <summary>
/// Convert a first-cumulant decay rate into a hydrodynamic diameter
/// via the Stokes–Einstein relation, given the standard DLS optics
/// (laser wavelength + scattering angle) and solvent properties.
/// </summary>
/// <remarks>
/// Equations:
///   q  = (4π·n / λ) · sin(θ/2)
///   D  = Γ / q²
///   d_h = k_B·T / (3·π·η·D)
/// All inputs are in physically convenient units (μs⁻¹, °C, mPa·s,
/// nm, °) and converted internally to SI before the calculation.
/// </remarks>
public static class StokesEinstein
{
    /// <summary>Boltzmann constant (J/K).</summary>
    public const double BoltzmannJoulePerKelvin = 1.380649e-23;

    /// <summary>
    /// Compute the hydrodynamic diameter implied by the supplied first
    /// cumulant. Returns a missing-metadata outcome when any of the
    /// solvent / optics parameters are null, listing the gaps so the
    /// UI can prompt the user.
    /// </summary>
    /// <param name="firstCumulantPerMicrosecond">Γ from the cumulant fit.</param>
    /// <param name="temperatureCelsius">Sample temperature.</param>
    /// <param name="viscosityMpas">Solvent dynamic viscosity (mPa·s).</param>
    /// <param name="refractiveIndex">Solvent refractive index at the laser wavelength.</param>
    /// <param name="wavelengthNm">Vacuum laser wavelength.</param>
    /// <param name="scatteringAngleDegrees">Detector scattering angle.</param>
    public static HydrodynamicSizeOutcome Compute(
        double firstCumulantPerMicrosecond,
        double? temperatureCelsius,
        double? viscosityMpas,
        double? refractiveIndex,
        double? wavelengthNm,
        double? scatteringAngleDegrees)
    {
        var missing = new List<string>();
        if (!IsValidPositive(temperatureCelsius is double t ? t + 273.15 : null))
            missing.Add("温度");
        if (!IsValidPositive(viscosityMpas))
            missing.Add("粘度");
        if (!IsValidPositive(refractiveIndex))
            missing.Add("屈折率");
        if (!IsValidPositive(wavelengthNm))
            missing.Add("波長");
        if (!IsValidAngle(scatteringAngleDegrees))
            missing.Add("散乱角");
        if (missing.Count > 0)
            return HydrodynamicSizeOutcome.MissingMetadata(missing);

        if (!double.IsFinite(firstCumulantPerMicrosecond) || firstCumulantPerMicrosecond <= 0)
            return HydrodynamicSizeOutcome.MissingMetadata(new[] { "Γ" });

        // Convert all inputs to SI before the physics.
        var tKelvin = temperatureCelsius!.Value + 273.15;
        var etaPaSecond = viscosityMpas!.Value * 1e-3;
        var lambdaMeter = wavelengthNm!.Value * 1e-9;
        var thetaRadian = scatteringAngleDegrees!.Value * Math.PI / 180.0;
        var gammaPerSecond = firstCumulantPerMicrosecond * 1e6;

        var q = (4.0 * Math.PI * refractiveIndex!.Value / lambdaMeter)
                * Math.Sin(thetaRadian / 2.0);
        var qSquared = q * q;
        if (!double.IsFinite(qSquared) || qSquared <= 0)
            return HydrodynamicSizeOutcome.MissingMetadata(new[] { "散乱角" });

        var diffusion = gammaPerSecond / qSquared;
        var diameterMeter = BoltzmannJoulePerKelvin * tKelvin
            / (3.0 * Math.PI * etaPaSecond * diffusion);
        var diameterNm = diameterMeter * 1e9;

        if (!double.IsFinite(diameterNm) || diameterNm <= 0)
            return HydrodynamicSizeOutcome.MissingMetadata(new[] { "Γ" });

        return HydrodynamicSizeOutcome.Ok(diameterNm, diffusion, q);
    }

    private static bool IsValidPositive(double? value)
        => value.HasValue && double.IsFinite(value.Value) && value.Value > 0;

    private static bool IsValidAngle(double? value)
        // 0° gives sin(0) = 0 → q² = 0 → divide by zero. Scattering
        // angles are physically defined on (0, 180] in DLS optics; in
        // (180, 360) sin(θ/2) decreases back through zero, which gives
        // a mathematically valid but physically wrong q. Reject those.
        => value.HasValue && double.IsFinite(value.Value) && value.Value > 0 && value.Value <= 180;
}
