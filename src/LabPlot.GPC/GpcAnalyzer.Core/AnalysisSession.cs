namespace GpcAnalyzer.Core;

public sealed class AnalysisSession
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    public string GeneratorName { get; set; } = "GPC Visualization";

    public bool Overlay { get; set; }

    public int ActiveDatasetIndex { get; set; } = -1;

    public List<AnalysisSessionDataset> Datasets { get; set; } = new();

    public AnalysisSessionCalibration? Calibration { get; set; }

    public AnalysisSessionMolecularWeight MolecularWeight { get; set; } = new();

    public AnalysisSessionAxes Axes { get; set; } = new();

    public AnalysisSessionLabels Labels { get; set; } = new();

    public GraphFormattingConfig? Formatting { get; set; }
}

public sealed class AnalysisSessionDataset
{
    public string SourceFilePath { get; set; } = string.Empty;

    public string? Detector { get; set; }

    public string? SelectedPeakId { get; set; }

    public AnalysisSessionStyle Style { get; set; } = new();
}

public sealed class AnalysisSessionStyle
{
    public string? ColorHex { get; set; }

    public string? LegendName { get; set; }

    public double LineWidth { get; set; } = GraphFormattingConfig.DefaultLineWidth;

    public double MarkerSize { get; set; } = GraphFormattingConfig.DefaultMarkerSize;
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

public sealed class AnalysisSessionAxes
{
    public string Mode { get; set; } = nameof(AnalysisSessionAxisMode.RetentionTime);

    public double? XMin { get; set; }

    public double? XMax { get; set; }

    public double? YMin { get; set; }

    public double? YMax { get; set; }
}

public enum AnalysisSessionAxisMode
{
    RetentionTime,
    MolecularWeight,
}

public sealed class AnalysisSessionLabels
{
    public string? Title { get; set; }

    public string? XLabel { get; set; }

    public string? YLabel { get; set; }
}
