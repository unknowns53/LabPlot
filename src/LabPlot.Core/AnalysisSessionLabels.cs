namespace LabPlot.Core;

/// <summary>
/// Title / axis label overrides persisted with the session. Same shape
/// across every LabPlot app.
/// </summary>
public sealed class AnalysisSessionLabels
{
    public string? Title { get; set; }

    public string? XLabel { get; set; }

    public string? YLabel { get; set; }
}
