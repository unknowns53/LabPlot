namespace LabPlot.Core;

/// <summary>
/// WPF-independent ScottPlot helpers shared by every LabPlot app. The
/// <c>Apply*</c> family takes a <see cref="GraphFormattingConfigBase"/> snapshot
/// and writes it onto a <see cref="ScottPlot.Plot"/>; the host MainWindow
/// is responsible for capturing the snapshot from its UI controls (typically
/// via <c>CaptureFormattingConfigFromControls()</c>) and calling
/// <see cref="ApplyAll"/> from its <c>ApplyPlotAppearance</c> entry point.
/// Because nothing here touches WPF types, the same code path runs from a
/// future Avalonia or CLI front-end.
/// </summary>
/// <remarks>
/// Tick base values were tuned together with the export scale path: at
/// scale = 1 they match the on-screen sizes the user sees in the WPF
/// preview, and at scale ≈ 3.125 (PNG export at 96 → 300 DPI) they keep
/// the same visual proportion. <see cref="ConfigureTickMarkStyle"/>
/// always re-derives length / width from base × scale so the output is
/// independent of any prior tick-style state.
/// </remarks>
public static class PlotAppearance
{
    public const float MajorTickLengthBase = 4f;

    public const float MajorTickWidthBase = 1f;

    public const float MinorTickLengthBase = 2f;

    public const float MinorTickWidthBase = 1f;

    /// <summary>
    /// Resets a tick-mark style to <paramref name="lengthBase"/> ×
    /// <paramref name="scale"/> length and <paramref name="widthBase"/> ×
    /// <paramref name="scale"/> width. When <paramref name="visible"/> is
    /// false, length is forced to 0 so the tick line is hidden while the
    /// width stays at its base for any callers that want to keep the
    /// configured value (e.g. labels-only mode).
    /// <c>Hairline</c> is always disabled because it would otherwise pin
    /// the rendered width to 1 px regardless of the value above.
    /// </summary>
    public static void ConfigureTickMarkStyle(
        ScottPlot.TickMarkStyle style,
        float lengthBase,
        float widthBase,
        float scale,
        bool visible)
    {
        style.Length = visible ? lengthBase * scale : 0f;
        style.Width = widthBase * scale;
        style.Hairline = false;
    }

    /// <summary>
    /// Clears the cached <c>SKTypeface</c> on every label / tick-label
    /// style so ScottPlot re-resolves the typeface from
    /// <c>FontName + Bold/Italic</c> on the next render. <c>Plot.Font.Set</c>
    /// otherwise pins a fixed-weight typeface that ignores subsequent
    /// Bold / Italic changes.
    /// </summary>
    public static void ResetLabelFontTypeface(ScottPlot.Plot plot)
    {
        plot.Axes.Title.Label.Font = null;
        plot.Axes.Bottom.Label.Font = null;
        plot.Axes.Left.Label.Font = null;
        plot.Axes.Bottom.TickLabelStyle.Font = null;
        plot.Axes.Left.TickLabelStyle.Font = null;
    }

    /// <summary>
    /// Parses <paramref name="hex"/> as a ScottPlot color, falling back
    /// to <paramref name="fallbackHex"/> if the primary value is not a
    /// valid hex string. Use this for any color that comes from user
    /// input (config field, color picker) where an invalid value should
    /// degrade to the app default rather than throw.
    /// </summary>
    public static ScottPlot.Color ColorFromHex(string hex, string fallbackHex)
    {
        try
        {
            return ScottPlot.Color.FromHex(new[] { hex }).First();
        }
        catch
        {
            return ScottPlot.Color.FromHex(new[] { fallbackHex }).First();
        }
    }

    /// <summary>
    /// Applies every shared formatting concern (font, font size, grid,
    /// Y-axis tick labels, frame, tick marks, title, axis-label bold,
    /// background) onto <paramref name="plot"/> from
    /// <paramref name="config"/>. The order matches the original per-app
    /// implementation so that frame visibility correctly reads the
    /// up-to-date Y-axis tick label state.
    /// </summary>
    public static void ApplyAll(ScottPlot.Plot plot, GraphFormattingConfigBase config, float scale = 1f)
    {
        ApplyFont(plot, config);
        ApplyFontSize(plot, config, scale);
        ApplyGrid(plot, config);
        ApplyYAxisTickLabels(plot, config);
        ApplyFrame(plot, config, scale);
        ApplyTickMarks(plot, config, scale);
        ApplyTitleStyle(plot, config);
        ApplyAxisLabelStyle(plot, config);
        ApplyBackground(plot, config);
    }

    /// <summary>
    /// Re-derives bottom and left tick-mark length / width from
    /// base × <paramref name="scale"/> so the same source values produce
    /// matching geometry whether we are rendering on screen (scale = 1)
    /// or exporting at high DPI. Hiding the Y-axis tick labels also
    /// hides the Y-axis tick lines themselves, matching the on-screen
    /// "labels and ticks travel together" contract.
    /// </summary>
    public static void ApplyTickMarks(ScottPlot.Plot plot, GraphFormattingConfigBase config, float scale = 1f)
    {
        bool showMajor = config.ShowMajorTicks;
        bool showMinor = config.ShowMinorTicks;
        bool yAxisVisible = config.ShowYAxisTickLabels;

        ConfigureTickMarkStyle(plot.Axes.Bottom.MajorTickStyle, MajorTickLengthBase, MajorTickWidthBase, scale, showMajor);
        ConfigureTickMarkStyle(plot.Axes.Bottom.MinorTickStyle, MinorTickLengthBase, MinorTickWidthBase, scale, showMinor);
        ConfigureTickMarkStyle(plot.Axes.Left.MajorTickStyle, MajorTickLengthBase, MajorTickWidthBase, scale, showMajor && yAxisVisible);
        ConfigureTickMarkStyle(plot.Axes.Left.MinorTickStyle, MinorTickLengthBase, MinorTickWidthBase, scale, showMinor && yAxisVisible);
    }

    /// <summary>
    /// Applies title visibility and bold weight from <paramref name="config"/>.
    /// </summary>
    public static void ApplyTitleStyle(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        plot.Axes.Title.Label.IsVisible = config.ShowTitle;
        plot.Axes.Title.Label.Bold = config.TitleBold;
    }

    /// <summary>
    /// Applies axis-label bold weight (X and Y receive the same value)
    /// from <paramref name="config"/>.
    /// </summary>
    public static void ApplyAxisLabelStyle(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        plot.Axes.Bottom.Label.Bold = config.AxisLabelBold;
        plot.Axes.Left.Label.Bold = config.AxisLabelBold;
    }

    /// <summary>
    /// Paints the figure and data backgrounds with the same color, falling
    /// back to <see cref="GraphFormattingConfigBase.DefaultBackgroundColorHex"/>
    /// when the configured hex is invalid.
    /// </summary>
    public static void ApplyBackground(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        var color = ColorFromHex(config.BackgroundColorHex, GraphFormattingConfigBase.DefaultBackgroundColorHex);
        plot.FigureBackground.Color = color;
        plot.DataBackground.Color = color;
    }

    /// <summary>
    /// Applies the configured font name to <paramref name="plot"/>. When
    /// the name is null / empty, the plot falls back to ScottPlot's
    /// automatic font selection. <see cref="ResetLabelFontTypeface"/> is
    /// called afterwards so subsequent Bold / Italic changes are honoured
    /// instead of being shadowed by the cached typeface that
    /// <c>Plot.Font.Set</c> installs.
    /// </summary>
    public static void ApplyFont(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        var fontName = config.FontName;
        if (string.IsNullOrWhiteSpace(fontName))
        {
            plot.Font.Automatic();
            ResetLabelFontTypeface(plot);
            return;
        }

        try
        {
            plot.Font.Set(fontName);
        }
        catch
        {
            plot.Font.Automatic();
        }

        ResetLabelFontTypeface(plot);
    }

    /// <summary>
    /// Sets every label and tick-label font size from
    /// <paramref name="config"/> × <paramref name="scale"/>. The title is
    /// 2 pt larger than the base; tick labels and the legend sit one
    /// scaled point smaller, with a 6 × scale floor to stay readable at
    /// tiny scales.
    /// </summary>
    public static void ApplyFontSize(ScottPlot.Plot plot, GraphFormattingConfigBase config, float scale = 1f)
    {
        var fontSize = (float)config.FontSize * scale;
        plot.Axes.Title.Label.FontSize = fontSize + (2 * scale);
        plot.Axes.Bottom.Label.FontSize = fontSize;
        plot.Axes.Left.Label.FontSize = fontSize;
        plot.Axes.Bottom.TickLabelStyle.FontSize = Math.Max(6 * scale, fontSize - scale);
        plot.Axes.Left.TickLabelStyle.FontSize = Math.Max(6 * scale, fontSize - scale);
        plot.Legend.FontSize = config.LegendFontSize is { } legendFontSize
            ? Math.Max(6 * scale, (float)legendFontSize * scale)
            : Math.Max(6 * scale, fontSize - scale);
    }

    /// <summary>
    /// Toggles ScottPlot's grid on or off according to
    /// <see cref="GraphFormattingConfigBase.ShowGrid"/>.
    /// </summary>
    public static void ApplyGrid(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        if (config.ShowGrid)
        {
            plot.ShowGrid();
        }
        else
        {
            plot.HideGrid();
        }
    }

    /// <summary>
    /// Toggles Y-axis tick label visibility from
    /// <see cref="GraphFormattingConfigBase.ShowYAxisTickLabels"/>.
    /// <see cref="ApplyTickMarks"/> reads the same flag to keep the
    /// Y-axis tick lines in sync with the labels.
    /// </summary>
    public static void ApplyYAxisTickLabels(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        plot.Axes.Left.TickLabelStyle.IsVisible = config.ShowYAxisTickLabels;
    }

    /// <summary>
    /// Applies frame edge visibility, width and color. The bottom edge
    /// is always visible because every LabPlot app shows X-axis numerics
    /// at all times; the left edge follows
    /// <see cref="GraphFormattingConfigBase.ShowYAxisTickLabels"/> so the
    /// numbered axis keeps a frame even when the user hides the box; the
    /// top and right edges follow
    /// <see cref="GraphFormattingConfigBase.ShowPlotFrame"/>. Width and
    /// color are applied to all four edges; the per-edge IsVisible flags
    /// suppress drawing on the hidden edges.
    /// </summary>
    public static void ApplyFrame(ScottPlot.Plot plot, GraphFormattingConfigBase config, float scale = 1f)
    {
        bool frameVisible = config.ShowPlotFrame;
        bool yLabelsVisible = config.ShowYAxisTickLabels;

        plot.Axes.Bottom.FrameLineStyle.IsVisible = true;
        plot.Axes.Left.FrameLineStyle.IsVisible = frameVisible || yLabelsVisible;
        plot.Axes.Top.FrameLineStyle.IsVisible = frameVisible;
        plot.Axes.Right.FrameLineStyle.IsVisible = frameVisible;

        plot.Axes.FrameWidth((float)config.PlotFrameWidth * scale);
        plot.Axes.FrameColor(ColorFromHex(config.PlotFrameColorHex, GraphFormattingConfigBase.DefaultPlotFrameColorHex));
    }

    /// <summary>
    /// Applies legend visibility and placement from <paramref name="config"/>.
    /// <paramref name="autoShow"/> is the per-app auto-show decision (e.g.
    /// 2+ overlaid datasets, or any dataset has a custom legend name) that
    /// kicks in when <see cref="GraphFormattingConfigBase.LegendVisibility"/>
    /// is <c>null</c>/<c>"Auto"</c>. <c>"Always"</c> forces visible,
    /// <c>"Never"</c> forces hidden regardless of <paramref name="autoShow"/>.
    /// </summary>
    public static void ApplyLegend(ScottPlot.Plot plot, GraphFormattingConfigBase config, bool autoShow)
    {
        bool show = config.LegendVisibility switch
        {
            "Always" => true,
            "Never" => false,
            _ => autoShow,
        };
        plot.Legend.IsVisible = show;
        if (show)
        {
            plot.Legend.Alignment = MapLegendAlignment(config.LegendPosition);
        }
    }

    private static ScottPlot.Alignment MapLegendAlignment(string position) => position switch
    {
        "UpperRight" => ScottPlot.Alignment.UpperRight,
        "UpperLeft" => ScottPlot.Alignment.UpperLeft,
        "LowerRight" => ScottPlot.Alignment.LowerRight,
        "LowerLeft" => ScottPlot.Alignment.LowerLeft,
        "MiddleRight" => ScottPlot.Alignment.MiddleRight,
        _ => ScottPlot.Alignment.UpperRight,
    };
}
