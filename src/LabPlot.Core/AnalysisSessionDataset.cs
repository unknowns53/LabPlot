namespace LabPlot.Core;

/// <summary>
/// Shared per-dataset entry inside an <see cref="AnalysisSession"/>.
/// Holds the source file path and the visual style every LabPlot app
/// needs. App-specific subclasses extend this with fields like detector
/// or selected peak id (GPC); apps with no extra per-dataset state can
/// use this base type directly (Spectrum).
/// </summary>
public class AnalysisSessionDataset
{
    public string SourceFilePath { get; set; } = string.Empty;

    public AnalysisSessionStyle Style { get; set; } = new();
}
