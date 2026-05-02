using LabPlot.Core;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Spectrum-specific session payload extending <see cref="AnalysisSession"/>.
/// Spectrum has no axis-mode toggle and no calibration / molecular weight
/// blocks, so it just holds the dataset list (using the shared base type),
/// the shared axis ranges, and the Spectrum-specific formatting config
/// alongside the cross-app session metadata.
/// </summary>
public sealed class SpectrumAnalysisSession : AnalysisSession
{
    public SpectrumAnalysisSession()
    {
        GeneratorName = "Spectrum Visualization";
    }

    public List<AnalysisSessionDataset> Datasets { get; set; } = new();

    public AnalysisSessionAxes Axes { get; set; } = new();

    public GraphFormattingConfig? Formatting { get; set; }

    public override void EnsureDefaults()
    {
        Datasets ??= new List<AnalysisSessionDataset>();
        Axes ??= new AnalysisSessionAxes();
        Labels ??= new AnalysisSessionLabels();
        Formatting?.Normalize();
    }
}
