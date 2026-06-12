using LabPlot.Core;

namespace DataViewer.Core;

/// <summary>X-Y point pair exported for one viewer series.</summary>
public readonly record struct ViewerDataPoint(double X, double Y);

/// <summary>
/// Viewer-specific export row: one entry per visible series, carrying the
/// already-transformed X-Y trace exactly as plotted.
/// </summary>
public sealed class ViewerAnalysisExportEntry : AnalysisExportEntry
{
    public IReadOnlyList<ViewerDataPoint> Points { get; init; } = Array.Empty<ViewerDataPoint>();
}
