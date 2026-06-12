using LabPlot.Core;

namespace DataViewer.Core;

/// <summary>
/// Viewer axis state: the shared X / Y range overrides plus log-scale
/// toggles and the secondary (right) Y axis range and label. All ranges
/// are stored in data units; log10 conversion happens at plot time.
/// </summary>
public sealed class ViewerSessionAxes : AnalysisSessionAxes
{
    public bool XLogScale { get; set; }

    public bool YLogScale { get; set; }

    public bool Y2LogScale { get; set; }

    public double? Y2Min { get; set; }

    public double? Y2Max { get; set; }

    public string? Y2Label { get; set; }
}
