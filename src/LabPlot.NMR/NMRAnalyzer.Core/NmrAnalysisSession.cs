using LabPlot.Core;

namespace NMRAnalyzer.Core;

/// <summary>
/// NMR-specific session payload extending <see cref="AnalysisSession"/>.
/// Mirrors <c>SpectrumAnalysisSession</c> (dataset list + shared axes) and
/// adds the two pieces of NMR analysis state that must survive save/load:
/// the integration regions and the cumulative chemical-shift referencing.
/// </summary>
public sealed class NmrAnalysisSession : AnalysisSession
{
    public NmrAnalysisSession()
    {
        GeneratorName = "NMR Analyzer";
    }

    public List<AnalysisSessionDataset> Datasets { get; set; } = new();

    public AnalysisSessionAxes Axes { get; set; } = new();

    public List<NmrIntegrationRegion> IntegrationRegions { get; set; } = new();

    /// <summary>
    /// Cumulative ppm shift applied by chemical-shift referencing, re-applied
    /// on load so the saved view is reproduced exactly.
    /// </summary>
    public double ReferenceShiftPpm { get; set; }

    public override void EnsureDefaults()
    {
        Datasets ??= new List<AnalysisSessionDataset>();
        Axes ??= new AnalysisSessionAxes();
        Labels ??= new AnalysisSessionLabels();
        IntegrationRegions ??= new List<NmrIntegrationRegion>();
    }
}
