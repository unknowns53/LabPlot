using LabPlot.Core;

namespace GpcAnalyzer.Core;

/// <summary>
/// GPC-specific session payload extending <see cref="AnalysisSession"/>
/// with the calibration / molecular weight settings unique to the GPC app.
/// Datasets and axes use GPC-specific subclasses so the detector / selected
/// peak id and the retention-time vs molecular-weight axis-mode toggle
/// round-trip through the JSON store without polymorphic contracts.
/// </summary>
public sealed class GpcAnalysisSession : AnalysisSession
{
    public GpcAnalysisSession()
    {
        GeneratorName = "GPC Visualization";
    }

    public List<GpcAnalysisSessionDataset> Datasets { get; set; } = new();

    public AnalysisSessionCalibration? Calibration { get; set; }

    public AnalysisSessionMolecularWeight MolecularWeight { get; set; } = new();

    public GpcAnalysisSessionAxes Axes { get; set; } = new();

    public GraphFormattingConfig? Formatting { get; set; }

    public override void EnsureDefaults()
    {
        Datasets ??= new List<GpcAnalysisSessionDataset>();
        MolecularWeight ??= new AnalysisSessionMolecularWeight();
        Axes ??= new GpcAnalysisSessionAxes();
        Labels ??= new AnalysisSessionLabels();
        Formatting?.Normalize();
    }
}

public sealed class GpcAnalysisSessionDataset : AnalysisSessionDataset
{
    public string? Detector { get; set; }

    public string? SelectedPeakId { get; set; }
}

public sealed class GpcAnalysisSessionAxes : AnalysisSessionAxes
{
    public string Mode { get; set; } = nameof(AnalysisSessionAxisMode.RetentionTime);
}

public sealed class AnalysisSessionCalibration
{
    public string FilePath { get; set; } = string.Empty;

    public string? Solvent { get; set; }

    public string? Detector { get; set; }
}

public sealed class AnalysisSessionMolecularWeight
{
    public bool Enabled { get; set; }

    public string YMode { get; set; } = nameof(MolecularWeightYMode.Signal);

    public double MinMolecularWeight { get; set; } = MolecularWeightConverter.DefaultMinMolecularWeight;

    public double MaxMolecularWeight { get; set; } = MolecularWeightConverter.DefaultMaxMolecularWeight;
}

public enum AnalysisSessionAxisMode
{
    RetentionTime,
    MolecularWeight,
}
