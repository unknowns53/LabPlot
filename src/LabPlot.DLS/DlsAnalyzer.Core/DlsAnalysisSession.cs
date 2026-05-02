using LabPlot.Core;

namespace DlsAnalyzer.Core;

/// <summary>
/// DLS-specific session payload extending <see cref="AnalysisSession"/>
/// with the workbook path (DLS loads multiple datasets from a single
/// xlsx, unlike GPC / Spectrum where each dataset is its own file),
/// a list of per-sheet states, axis ranges, formatting config, and the
/// active distribution mode + run index.
/// </summary>
/// <remarks>
/// Restoring a session re-reads the xlsx referenced by
/// <see cref="WorkbookPath"/> and matches per-sheet state by
/// <see cref="DlsAnalysisSessionDataset.SheetName"/>. Sheets that no
/// longer exist in the workbook are reported as warnings rather than
/// silently dropped, so the user knows what was lost.
/// </remarks>
public sealed class DlsAnalysisSession : AnalysisSession
{
    public DlsAnalysisSession()
    {
        GeneratorName = "LabPlot DLS";
    }

    public string WorkbookPath { get; set; } = string.Empty;

    public List<DlsAnalysisSessionDataset> Datasets { get; set; } = new();

    public AnalysisSessionAxes Axes { get; set; } = new();

    public GraphFormattingConfig? Formatting { get; set; }

    public string SelectedDistributionMode { get; set; } = "Number";

    public int SelectedRunIndex { get; set; }

    public override void EnsureDefaults()
    {
        Datasets ??= new List<DlsAnalysisSessionDataset>();
        Axes ??= new AnalysisSessionAxes();
        Labels ??= new AnalysisSessionLabels();
        Formatting?.Normalize();
        foreach (var ds in Datasets)
        {
            ds.Metadata ??= new DlsAnalysisSessionMetadata();
            ds.CumulantSettings ??= new DlsAnalysisSessionCumulantSettings();
            ds.Style ??= new AnalysisSessionStyle();
        }
    }
}

/// <summary>
/// Per-sheet session entry. Carries the sheet name (used as the match
/// key when restoring state onto a re-loaded workbook), the selection
/// flag, the visual style (Color / LegendName / LineWidth / MarkerSize),
/// the editable measurement metadata, and the cumulant fit range.
/// </summary>
/// <remarks>
/// <see cref="AnalysisSessionDataset.SourceFilePath"/> is set to the
/// parent workbook path (same value across all entries in a session)
/// so existing GPC / Spectrum-shaped tooling can still see a non-empty
/// source if it ever traverses the base type.
/// </remarks>
public sealed class DlsAnalysisSessionDataset : AnalysisSessionDataset
{
    public string SheetName { get; set; } = string.Empty;

    public bool Selected { get; set; }

    public DlsAnalysisSessionMetadata Metadata { get; set; } = new();

    public DlsAnalysisSessionCumulantSettings CumulantSettings { get; set; } = new();
}

/// <summary>
/// Persisted measurement metadata. Mirrors the runtime
/// DlsDatasetMetadataState shape (in the WPF layer); kept here so the
/// JSON contract stays in DlsAnalyzer.Core where session save / load
/// lives.
/// </summary>
public sealed class DlsAnalysisSessionMetadata
{
    public double? TemperatureCelsius { get; set; }
    public string? Solvent { get; set; }
    public double? ConcentrationMgPerMl { get; set; }
    public double? RefractiveIndex { get; set; }
    public double? ViscosityMpas { get; set; }
    public double? WavelengthNm { get; set; }
    public double? ScatteringAngleDegrees { get; set; }
}

/// <summary>
/// Persisted cumulant fit range. Both null = auto-detect the τ window
/// at load time (default behaviour).
/// </summary>
public sealed class DlsAnalysisSessionCumulantSettings
{
    public double? FitRangeMinMicroseconds { get; set; }
    public double? FitRangeMaxMicroseconds { get; set; }
}
