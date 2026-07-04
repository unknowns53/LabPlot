using LabPlot.Core;

namespace NMRAnalyzer.Core;

/// <summary>One (ppm, intensity) sample in an NMR export.</summary>
public readonly record struct NmrDataPoint(double Ppm, double Intensity);

/// <summary>
/// NMR-specific analysis export row. Inherits the shared display / source /
/// axis-label fields from <see cref="AnalysisExportEntry"/> and adds the
/// ppm-intensity point payload that NMR reports produce.
/// </summary>
public sealed class NmrAnalysisExportEntry : AnalysisExportEntry
{
    public IReadOnlyList<NmrDataPoint> Points { get; init; } = Array.Empty<NmrDataPoint>();
}
