namespace LabPlot.Core;

/// <summary>
/// Per-dataset visual style (color, legend label, line / marker sizes)
/// shared verbatim across LabPlot apps. Defaults pull from
/// <see cref="GraphFormattingConfigBase"/> so a fresh entry mirrors the
/// app-wide formatting baseline.
/// </summary>
public sealed class AnalysisSessionStyle
{
    public string? ColorHex { get; set; }

    public string? LegendName { get; set; }

    public double LineWidth { get; set; } = GraphFormattingConfigBase.DefaultLineWidth;

    public double MarkerSize { get; set; } = GraphFormattingConfigBase.DefaultMarkerSize;
}
