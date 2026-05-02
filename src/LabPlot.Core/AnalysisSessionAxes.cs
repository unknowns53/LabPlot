namespace LabPlot.Core;

/// <summary>
/// Shared axis range overrides (X / Y min / max) persisted with the
/// session. Subclasses extend this with app-specific axis-mode toggles
/// (e.g. retention time vs molecular weight in GPC); apps with no extra
/// axis state can use this base type directly (Spectrum).
/// </summary>
public class AnalysisSessionAxes
{
    public double? XMin { get; set; }

    public double? XMax { get; set; }

    public double? YMin { get; set; }

    public double? YMax { get; set; }
}
