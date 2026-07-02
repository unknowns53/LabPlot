using LabPlot.Core;

namespace DataViewer.Core;

/// <summary>
/// Session container for the generic data viewer (persisted as
/// <c>.gvjson</c>). File-backed tables are stored as path references and
/// re-read on load; clipboard-pasted tables embed their numeric payload
/// via <see cref="ViewerEmbeddedTable"/> so they survive the round-trip.
/// </summary>
public sealed class ViewerAnalysisSession : AnalysisSession
{
    public ViewerAnalysisSession()
    {
        GeneratorName = "Data Viewer";
    }

    public List<ViewerSessionDataset> Datasets { get; set; } = new();

    public ViewerSessionAxes Axes { get; set; } = new();

    public GraphFormattingConfig? Formatting { get; set; }

    public override void EnsureDefaults()
    {
        Datasets ??= new List<ViewerSessionDataset>();
        Datasets.RemoveAll(static dataset => dataset is null);
        foreach (var dataset in Datasets)
        {
            dataset.Series ??= new List<ViewerSessionSeries>();
            dataset.Series.RemoveAll(static series => series is null);
        }

        Axes ??= new ViewerSessionAxes();
        Labels ??= new AnalysisSessionLabels();
        Formatting?.Normalize();
    }
}

/// <summary>
/// One loaded table inside a viewer session. <c>SourceFilePath</c> (from
/// the shared base) is empty for clipboard tables, which carry their data
/// in <see cref="EmbeddedTable"/> instead.
/// </summary>
public sealed class ViewerSessionDataset : AnalysisSessionDataset
{
    public string? SheetName { get; set; }

    public int XColumnIndex { get; set; }

    public List<ViewerSessionSeries> Series { get; set; } = new();

    public ViewerEmbeddedTable? EmbeddedTable { get; set; }
}

/// <summary>
/// One Y series (column selection) inside a session dataset.
/// <see cref="ColumnName"/> is stored alongside the index so a re-read
/// file whose columns moved can be re-matched by name.
/// </summary>
public sealed class ViewerSessionSeries
{
    public int ColumnIndex { get; set; }

    public string? ColumnName { get; set; }

    /// <summary>
    /// Flat display-order key across all series in the session (drawing /
    /// legend / auto-color order). Missing in older session files, which
    /// deserialize it as 0 for every series - combined with
    /// <see cref="SeriesOrderPlanner.FlattenInDisplayOrder{T}"/>'s stable
    /// sort, that reproduces the legacy "table then column" order unchanged.
    /// </summary>
    public int DisplayOrder { get; set; }

    public bool IsVisible { get; set; } = true;

    /// <summary>"Left" (default) or "Right".</summary>
    public string AxisSide { get; set; } = "Left";

    public bool Normalize { get; set; }

    public double YOffset { get; set; }

    public int SmoothingWindow { get; set; }

    /// <summary>Plot style token (see <see cref="ViewerChartType"/>). Missing / unknown ⇒ "Line".</summary>
    public string ChartType { get; set; } = ViewerChartType.Line.ToToken();

    public AnalysisSessionStyle Style { get; set; } = new();
}
