namespace SpectrumAnalyzer.Core;

/// <summary>
/// A single IR (or wavenumber-axis) peak assignment used as an annotation
/// on the spectrum plot. A range is expressed by setting
/// <see cref="MinWavenumber"/> and <see cref="MaxWavenumber"/> to the band
/// limits; a single-value assignment uses the same number for both.
/// </summary>
public sealed record PeakAssignment
{
    public required string Label { get; init; }

    public required double MinWavenumber { get; init; }

    public required double MaxWavenumber { get; init; }

    /// <summary>
    /// Hex color (e.g. <c>"#DC2626"</c>) used to tint the band, line, and
    /// label so multiple assignments can be distinguished at a glance.
    /// </summary>
    public string ColorHex { get; init; } = "#94A3B8";

    public bool IsRange => Math.Abs(MaxWavenumber - MinWavenumber) > 1e-9;

    public double CenterWavenumber => (MinWavenumber + MaxWavenumber) / 2.0;
}

/// <summary>
/// Built-in IR peak assignment table covering the functional groups that
/// show up most often in the user's polymer / acetylene research. The list
/// is intentionally short; users overlay only the ones they need.
/// </summary>
public static class IrPeakAssignmentTable
{
    public static IReadOnlyList<PeakAssignment> Default { get; } = new PeakAssignment[]
    {
        new() { Label = "O-H stretch",        MinWavenumber = 3200, MaxWavenumber = 3600, ColorHex = "#2563EB" },
        new() { Label = "N-H stretch",        MinWavenumber = 3300, MaxWavenumber = 3500, ColorHex = "#1D4ED8" },
        new() { Label = "≡C-H stretch",       MinWavenumber = 3260, MaxWavenumber = 3340, ColorHex = "#0891B2" },
        new() { Label = "C-H stretch (sp3)",  MinWavenumber = 2850, MaxWavenumber = 2960, ColorHex = "#EA580C" },
        new() { Label = "C≡C stretch",        MinWavenumber = 2100, MaxWavenumber = 2260, ColorHex = "#16A34A" },
        new() { Label = "C≡N stretch",        MinWavenumber = 2200, MaxWavenumber = 2260, ColorHex = "#65A30D" },
        new() { Label = "C=O stretch",        MinWavenumber = 1680, MaxWavenumber = 1750, ColorHex = "#DC2626" },
        new() { Label = "N-H bend (amide II)", MinWavenumber = 1500, MaxWavenumber = 1600, ColorHex = "#9333EA" },
        new() { Label = "Aromatic C=C",       MinWavenumber = 1450, MaxWavenumber = 1610, ColorHex = "#7C3AED" },
        new() { Label = "C-O stretch",        MinWavenumber = 1000, MaxWavenumber = 1300, ColorHex = "#475569" },
    };
}
