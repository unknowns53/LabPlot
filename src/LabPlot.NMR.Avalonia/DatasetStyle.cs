using LabPlot.Core;

namespace LabPlot.NMR.Avalonia;

/// <summary>
/// Per-dataset visual style for an overlaid NMR spectrum. Mirrors
/// Spectrum.Avalonia's DatasetStyle, but the defaults come straight from
/// <see cref="GraphFormattingConfigBase"/> constants since the NMR module
/// has no per-app GraphFormattingConfig / "finishing" tab.
/// </summary>
internal sealed class DatasetStyle
{
    public string? ColorHex { get; set; }

    public string? LegendName { get; set; }

    public double LineWidth { get; set; } = GraphFormattingConfigBase.DefaultLineWidth;

    public double MarkerSize { get; set; } = GraphFormattingConfigBase.DefaultMarkerSize;

    /// <summary>Display-only vertical scale (1 = raw). Set by "normalize".</summary>
    public double YScale { get; set; } = 1.0;

    /// <summary>Display-only vertical offset (0 = none). Set by "stack".</summary>
    public double YOffset { get; set; }
}
