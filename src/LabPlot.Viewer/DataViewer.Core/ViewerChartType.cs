namespace DataViewer.Core;

/// <summary>
/// Per-series plot style for the generic viewer. Three of the four share the
/// same ScottPlot <c>Scatter</c> plottable (toggling line width / marker
/// visibility); <see cref="Bar"/> uses a separate <c>Bars</c> plottable.
/// </summary>
public enum ViewerChartType
{
    /// <summary>Connected line, no markers (the default, matches legacy output).</summary>
    Line,

    /// <summary>Markers only, no connecting line.</summary>
    Markers,

    /// <summary>Connected line with markers at each point.</summary>
    LineMarkers,

    /// <summary>Vertical bars from a zero baseline.</summary>
    Bar,
}

/// <summary>
/// Token (de)serialization for <see cref="ViewerChartType"/>. Session JSON
/// stores the enum name; unknown / missing tokens fall back to
/// <see cref="ViewerChartType.Line"/> so older sessions open unchanged.
/// </summary>
public static class ViewerChartTypes
{
    public static string ToToken(this ViewerChartType type) => type.ToString();

    public static ViewerChartType Parse(string? token)
        => Enum.TryParse<ViewerChartType>(token, ignoreCase: true, out var value)
            && Enum.IsDefined(value)
            ? value
            : ViewerChartType.Line;

    /// <summary>True when the type renders markers (and thus needs a non-zero size).</summary>
    public static bool ShowsMarkers(this ViewerChartType type)
        => type is ViewerChartType.Markers or ViewerChartType.LineMarkers;

    /// <summary>True when the type renders a connecting line.</summary>
    public static bool ShowsLine(this ViewerChartType type)
        => type is ViewerChartType.Line or ViewerChartType.LineMarkers;
}
