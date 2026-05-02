namespace LabPlot.Core;

/// <summary>
/// Shared base type for the per-app <c>GraphFormattingConfig</c> records that
/// drive font / frame / background / line styling on every LabPlot plot.
/// </summary>
/// <remarks>
/// Each LabPlot app keeps its own <c>GraphFormattingConfig</c> subclass so it
/// can declare app-specific persistence fields (calibration paths, axis
/// override modes, integration regions, λmax / cloud-point markers, ...) that
/// have no analogue in the other apps. The properties below are the ones every
/// app shares verbatim, including default values, ranges and normalization
/// rules. Subclasses should call <c>base.Normalize()</c> from their own
/// <c>Normalize()</c> override before validating their app-specific fields.
/// XAML bindings on the WPF side reference these properties through the
/// subclass instance, so existing binding paths continue to work unchanged.
/// </remarks>
public abstract class GraphFormattingConfigBase
{
    public const double DefaultFontSize = 12;
    public const double DefaultLineWidth = 1.5;
    public const double DefaultMarkerSize = 0;
    public const double DefaultPlotFrameWidth = 1;
    public const string DefaultPlotFrameColorHex = "#475569";
    public const string DefaultBackgroundColorHex = "#FFFFFF";

    public string? FontName { get; set; }
    public double FontSize { get; set; } = DefaultFontSize;
    public bool ShowGrid { get; set; } = true;
    public bool ShowYAxisTickLabels { get; set; } = true;
    public bool ShowMajorTicks { get; set; } = true;
    public bool ShowMinorTicks { get; set; } = true;
    public bool ShowPlotFrame { get; set; } = true;
    public double PlotFrameWidth { get; set; } = DefaultPlotFrameWidth;
    public string PlotFrameColorHex { get; set; } = DefaultPlotFrameColorHex;
    public string BackgroundColorHex { get; set; } = DefaultBackgroundColorHex;
    public bool ShowTitle { get; set; } = true;
    public bool TitleBold { get; set; } = true;
    public bool AxisLabelBold { get; set; }
    public string? AspectRatio { get; set; }
    public string? DefaultLineColorHex { get; set; }
    public double LineWidth { get; set; } = DefaultLineWidth;
    public double MarkerSize { get; set; } = DefaultMarkerSize;

    /// <summary>
    /// User preference for the directory the export dialogs open to. Persisted
    /// alongside the formatting defaults so it survives app restarts.
    /// </summary>
    public string? DefaultOutputDirectory { get; set; }

    /// <summary>
    /// Validates and snaps the shared formatting fields back into their
    /// expected ranges. Subclasses must call <c>base.Normalize()</c> from
    /// their own override before normalizing any app-specific fields.
    /// </summary>
    public virtual void Normalize()
    {
        FontName = ConfigNormalizer.NormalizeOptionalText(FontName);
        AspectRatio = ConfigNormalizer.NormalizeOptionalText(AspectRatio);
        DefaultLineColorHex = ConfigNormalizer.NormalizeOptionalHex(DefaultLineColorHex);
        DefaultOutputDirectory = ConfigNormalizer.NormalizeOptionalText(DefaultOutputDirectory);

        if (!ConfigNormalizer.IsPositive(FontSize))
        {
            FontSize = DefaultFontSize;
        }

        if (!ConfigNormalizer.IsPositive(PlotFrameWidth))
        {
            PlotFrameWidth = DefaultPlotFrameWidth;
        }

        if (!ConfigNormalizer.IsHexColor(PlotFrameColorHex))
        {
            PlotFrameColorHex = DefaultPlotFrameColorHex;
        }

        if (!ConfigNormalizer.IsHexColor(BackgroundColorHex))
        {
            BackgroundColorHex = DefaultBackgroundColorHex;
        }

        if (!ConfigNormalizer.IsPositive(LineWidth))
        {
            LineWidth = DefaultLineWidth;
        }

        if (!ConfigNormalizer.IsNonNegative(MarkerSize))
        {
            MarkerSize = DefaultMarkerSize;
        }
    }

    public string FormatFontSize()
    {
        return ConfigNormalizer.FormatNumber(FontSize);
    }

    public string FormatFrameWidth()
    {
        return ConfigNormalizer.FormatNumber(PlotFrameWidth);
    }

    public string FormatLineWidth()
    {
        return ConfigNormalizer.FormatNumber(LineWidth);
    }

    public string FormatMarkerSize()
    {
        return ConfigNormalizer.FormatNumber(MarkerSize);
    }
}
