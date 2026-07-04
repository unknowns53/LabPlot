namespace NMRAnalyzer.Core;

/// <summary>
/// How to subtract a baseline before integrating an NMR region. Kept as an
/// enum (rather than a bool) so curved-baseline modes can be added later,
/// but only the two modes an NMR area ratio actually needs are implemented —
/// the IR integrator's convex-hull / rubber-band / polynomial variants are
/// overkill for a phase-corrected spectrum.
/// </summary>
public enum NmrBaselineMode
{
    /// <summary>Integrate the raw intensities as-is.</summary>
    None = 0,

    /// <summary>
    /// Subtract the straight line through the region's two endpoints before
    /// integrating — the standard NMR integral baseline.
    /// </summary>
    Linear = 1,
}

/// <summary>
/// User-defined integration region: a labelled ppm range with a baseline
/// choice. <see cref="PpmMin"/> / <see cref="PpmMax"/> are ordered
/// ascending regardless of the display convention (high ppm on the left).
/// </summary>
public sealed record NmrIntegrationRegion
{
    public required string Label { get; init; }

    public required double PpmMin { get; init; }

    public required double PpmMax { get; init; }

    public NmrBaselineMode Baseline { get; init; } = NmrBaselineMode.Linear;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Label)
        && double.IsFinite(PpmMin)
        && double.IsFinite(PpmMax)
        && PpmMin < PpmMax;
}

/// <summary>
/// Result of integrating one <see cref="NmrIntegrationRegion"/>.
/// <see cref="Ratio"/> is filled in by
/// <see cref="NmrIntegrator.NormalizeToReference"/>; a bare
/// <see cref="NmrIntegrator.Integrate"/> leaves it NaN.
/// </summary>
public sealed record NmrIntegrationResult
{
    public required NmrIntegrationRegion Region { get; init; }

    public required double Area { get; init; }

    public required double RawArea { get; init; }

    public required double BaselineArea { get; init; }

    public required int PointCount { get; init; }

    /// <summary>Area relative to a reference region; NaN until normalized.</summary>
    public double Ratio { get; init; } = double.NaN;

    public bool HasResult => PointCount >= 2 && double.IsFinite(Area);
}
