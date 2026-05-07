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
    public const double DefaultFontSize = 16;
    public const double DefaultLineWidth = 1.5;
    public const double DefaultMarkerSize = 0;
    public const double DefaultPlotFrameWidth = 1;
    public const double DefaultTickWidth = 1;
    public const string DefaultPlotFrameColorHex = "#475569";
    public const string DefaultBackgroundColorHex = "#FFFFFF";
    public const string DefaultLegendPositionValue = "UpperRight";

    /// <summary>
    /// Maximum absolute pixel offset accepted for
    /// <see cref="LegendOffsetX"/> / <see cref="LegendOffsetY"/>. Values
    /// outside <c>[-LegendOffsetLimit, +LegendOffsetLimit]</c> snap back to
    /// 0 so a fat-finger entry cannot fling the legend hundreds of pixels
    /// off-canvas. Tuned so the maximum excursion comfortably reaches any
    /// corner of a typical figure (most plots are well under 2000 px tall).
    /// </summary>
    public const double LegendOffsetLimit = 2000.0;

    /// <summary>
    /// Multiplier applied to ScottPlot's <c>NumericAutomatic.TickDensity</c>
    /// for axes still using the automatic generator. 1.0 = ScottPlot stock
    /// density (felt too crowded across all three apps with the default
    /// 14 pt tick labels), 0.5 = halved density (current shipping default),
    /// values down to <see cref="MinTickDensity"/> let users dial it
    /// further sparse on screen. <see cref="MaxTickDensity"/> caps the
    /// upper bound so a fat-finger value cannot push ScottPlot into a
    /// runaway "fit as many ticks as possible" state.
    /// </summary>
    public const double DefaultTickDensity = 0.5;

    public const double MinTickDensity = 0.1;
    public const double MaxTickDensity = 2.0;

    public string? FontName { get; set; }
    public double FontSize { get; set; } = DefaultFontSize;
    public bool ShowGrid { get; set; } = true;
    public bool ShowYAxisTickLabels { get; set; } = true;
    public bool ShowMajorTicks { get; set; } = true;
    public bool ShowMinorTicks { get; set; } = true;
    public double TickDensity { get; set; } = DefaultTickDensity;

    /// <summary>
    /// Line width applied to both major and minor tick marks. The two
    /// share a single setting because users invariably want them to scale
    /// together — only the (constant) tick lengths differ.
    /// </summary>
    public double TickWidth { get; set; } = DefaultTickWidth;
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
    /// Legend visibility override. <c>null</c> or <c>"Auto"</c> defers to
    /// the per-app auto-show heuristic (e.g. multiple datasets overlaid, or
    /// at least one dataset with a custom legend name); <c>"Always"</c>
    /// forces it on; <c>"Never"</c> hides it regardless of dataset count.
    /// Any other value normalizes back to <c>null</c>.
    /// </summary>
    public string? LegendVisibility { get; set; }

    /// <summary>
    /// Optional legend font size. When null, the legend keeps the historical
    /// derived size of one point smaller than the graph base font size.
    /// </summary>
    public double? LegendFontSize { get; set; }

    /// <summary>
    /// Legend placement, mapped 1:1 onto <c>ScottPlot.Alignment</c>. One of
    /// the nine corner / center anchors (<c>"UpperLeft"</c>,
    /// <c>"UpperCenter"</c>, <c>"UpperRight"</c>, <c>"MiddleLeft"</c>,
    /// <c>"MiddleCenter"</c>, <c>"MiddleRight"</c>, <c>"LowerLeft"</c>,
    /// <c>"LowerCenter"</c>, <c>"LowerRight"</c>). Any other value
    /// normalizes back to <see cref="DefaultLegendPositionValue"/>.
    /// Edge-anchored "outside" placements are not exposed because they
    /// need a different ScottPlot API path.
    /// </summary>
    public string LegendPosition { get; set; } = DefaultLegendPositionValue;

    /// <summary>
    /// Horizontal pixel offset relative to the anchor selected by
    /// <see cref="LegendPosition"/>. Positive values move the legend
    /// rightwards, negative values leftwards. Combined with
    /// <see cref="LegendOffsetY"/> via <c>ScottPlot.Legend.Margin</c> in
    /// <c>PlotAppearance.ApplyLegend</c> so users can nudge the legend
    /// off the chosen corner without leaving the anchor system.
    /// </summary>
    public double LegendOffsetX { get; set; }

    /// <summary>
    /// Vertical pixel offset relative to the anchor selected by
    /// <see cref="LegendPosition"/>. Positive values move the legend
    /// downwards, negative values upwards.
    /// </summary>
    public double LegendOffsetY { get; set; }

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
        LegendVisibility = NormalizeLegendVisibility(LegendVisibility);
        LegendPosition = NormalizeLegendPosition(LegendPosition);

        if (!ConfigNormalizer.IsPositive(FontSize))
        {
            FontSize = DefaultFontSize;
        }

        if (LegendFontSize is { } legendFontSize && !ConfigNormalizer.IsPositive(legendFontSize))
        {
            LegendFontSize = null;
        }

        if (!ConfigNormalizer.IsPositive(PlotFrameWidth))
        {
            PlotFrameWidth = DefaultPlotFrameWidth;
        }

        if (!ConfigNormalizer.IsPositive(TickWidth))
        {
            TickWidth = DefaultTickWidth;
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

        if (!double.IsFinite(TickDensity) || TickDensity < MinTickDensity || TickDensity > MaxTickDensity)
        {
            TickDensity = DefaultTickDensity;
        }

        LegendOffsetX = ClampLegendOffset(LegendOffsetX);
        LegendOffsetY = ClampLegendOffset(LegendOffsetY);
    }

    private static double ClampLegendOffset(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0.0;
        }

        if (value < -LegendOffsetLimit) return -LegendOffsetLimit;
        if (value > LegendOffsetLimit) return LegendOffsetLimit;
        return value;
    }

    public string FormatFontSize()
    {
        return ConfigNormalizer.FormatNumber(FontSize);
    }

    public string FormatLegendFontSize()
    {
        return LegendFontSize is { } legendFontSize
            ? ConfigNormalizer.FormatNumber(legendFontSize)
            : string.Empty;
    }

    public string FormatFrameWidth()
    {
        return ConfigNormalizer.FormatNumber(PlotFrameWidth);
    }

    public string FormatTickWidth()
    {
        return ConfigNormalizer.FormatNumber(TickWidth);
    }

    public string FormatLineWidth()
    {
        return ConfigNormalizer.FormatNumber(LineWidth);
    }

    public string FormatTickDensity()
    {
        return ConfigNormalizer.FormatNumber(TickDensity);
    }

    public string FormatMarkerSize()
    {
        return ConfigNormalizer.FormatNumber(MarkerSize);
    }

    private static string? NormalizeLegendVisibility(string? text)
    {
        var normalized = ConfigNormalizer.NormalizeOptionalText(text);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (normalized.Equals("Always", StringComparison.OrdinalIgnoreCase))
        {
            return "Always";
        }

        if (normalized.Equals("Never", StringComparison.OrdinalIgnoreCase))
        {
            return "Never";
        }

        return null;
    }

    private static string NormalizeLegendPosition(string? text)
    {
        var normalized = ConfigNormalizer.NormalizeOptionalText(text);
        if (normalized is null)
        {
            return DefaultLegendPositionValue;
        }

        if (normalized.Equals("UpperLeft", StringComparison.OrdinalIgnoreCase)) return "UpperLeft";
        if (normalized.Equals("UpperCenter", StringComparison.OrdinalIgnoreCase)) return "UpperCenter";
        if (normalized.Equals("UpperRight", StringComparison.OrdinalIgnoreCase)) return "UpperRight";
        if (normalized.Equals("MiddleLeft", StringComparison.OrdinalIgnoreCase)) return "MiddleLeft";
        if (normalized.Equals("MiddleCenter", StringComparison.OrdinalIgnoreCase)) return "MiddleCenter";
        if (normalized.Equals("MiddleRight", StringComparison.OrdinalIgnoreCase)) return "MiddleRight";
        if (normalized.Equals("LowerLeft", StringComparison.OrdinalIgnoreCase)) return "LowerLeft";
        if (normalized.Equals("LowerCenter", StringComparison.OrdinalIgnoreCase)) return "LowerCenter";
        if (normalized.Equals("LowerRight", StringComparison.OrdinalIgnoreCase)) return "LowerRight";

        return DefaultLegendPositionValue;
    }

    public string FormatLegendOffsetX()
    {
        return ConfigNormalizer.FormatNumber(LegendOffsetX);
    }

    public string FormatLegendOffsetY()
    {
        return ConfigNormalizer.FormatNumber(LegendOffsetY);
    }
}
