namespace SpectrumAnalyzer.Core;

public sealed class AnalysisSession
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    public string GeneratorName { get; set; } = "Spectrum Visualization";

    public bool Overlay { get; set; }

    public int ActiveDatasetIndex { get; set; } = -1;

    public List<AnalysisSessionDataset> Datasets { get; set; } = new();

    public AnalysisSessionAxes Axes { get; set; } = new();

    public AnalysisSessionLabels Labels { get; set; } = new();

    public GraphFormattingConfig? Formatting { get; set; }
}

public sealed class AnalysisSessionDataset
{
    public string SourceFilePath { get; set; } = string.Empty;

    public AnalysisSessionStyle Style { get; set; } = new();
}

public sealed class AnalysisSessionStyle
{
    public string? ColorHex { get; set; }

    public string? LegendName { get; set; }

    public double LineWidth { get; set; } = GraphFormattingConfig.DefaultLineWidth;

    public double MarkerSize { get; set; } = GraphFormattingConfig.DefaultMarkerSize;
}

public sealed class AnalysisSessionAxes
{
    public double? XMin { get; set; }

    public double? XMax { get; set; }

    public double? YMin { get; set; }

    public double? YMax { get; set; }
}

public sealed class AnalysisSessionLabels
{
    public string? Title { get; set; }

    public string? XLabel { get; set; }

    public string? YLabel { get; set; }
}
