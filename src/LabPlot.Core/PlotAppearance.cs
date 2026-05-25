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
/// independent of any prior tick-style state. The width side became
/// user-tunable in 2026-05; both major and minor tick widths now read
/// from the single <see cref="GraphFormattingConfigBase.TickWidth"/>
/// (users always want the two to scale together) rather than a fixed
/// constant.
/// </remarks>
public static class PlotAppearance
{
    public const float MajorTickLengthBase = 8f;

    public const float MinorTickLengthBase = 4f;

    /// <summary>
    /// Grid line color used by <see cref="ApplyGrid"/> when the grid is
    /// visible. Replaces ScottPlot's stock light-grey grid (#CCCCCC) with
    /// a softer slate-grey (#E5E7EB) that matches the LabPlot sidebar
    /// border so the gridlines no longer compete visually with the data.
    /// </summary>
    public const string DefaultGridColorHex = "#E5E7EB";

    /// <summary>
    /// Legend background color used by <see cref="ApplyLegend"/>. White
    /// with full opacity reads cleanly over the white plot background
    /// while staying distinct via the outline.
    /// </summary>
    public const string DefaultLegendBackgroundColorHex = "#FFFFFF";

    /// <summary>
    /// Legend outline color used by <see cref="ApplyLegend"/>. Matches
    /// the LabPlot sidebar border (#CBD5E1) so the legend frame feels
    /// like part of the surrounding chrome instead of a stock ScottPlot
    /// black border.
    /// </summary>
    public const string DefaultLegendOutlineColorHex = "#CBD5E1";

    /// <summary>
    /// Legend outline width in pixels at scale = 1.
    /// </summary>
    public const float DefaultLegendOutlineWidth = 1f;

    /// <summary>
    /// Default multiplier applied to ScottPlot's
    /// <c>NumericAutomatic.TickDensity</c> when no config value is supplied.
    /// The shipping value lives on <see cref="GraphFormattingConfigBase.DefaultTickDensity"/>;
    /// this constant exists so direct callers of
    /// <see cref="ApplyTickDensity"/> (without a config in hand) still get
    /// the same baseline density as <see cref="ApplyAll"/>.
    /// </summary>
    public const double DefaultTickDensity = GraphFormattingConfigBase.DefaultTickDensity;

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
        ApplyTickDensity(plot, config.TickDensity);
        ApplyTitleStyle(plot, config);
        ApplyAxisLabelStyle(plot, config);
        ApplyBackground(plot, config);
    }

    /// <summary>
    /// Reduces ScottPlot's automatic tick density on the bottom and left
    /// axes to <paramref name="density"/>× of the default. Only acts on
    /// axes whose <c>TickGenerator</c> is still a
    /// <c>NumericAutomatic</c>; axes that have been replaced with
    /// <c>NumericManual</c> (e.g. DLS log size axis, GPC molecular-weight
    /// log axis) keep their hand-built tick set untouched. Safe to call
    /// from <see cref="ApplyAll"/> on every refresh — setting the
    /// existing instance's <c>TickDensity</c> property is idempotent and
    /// does not allocate. Caller is expected to pass a value already
    /// clamped to <c>[GraphFormattingConfigBase.MinTickDensity,
    /// GraphFormattingConfigBase.MaxTickDensity]</c> via
    /// <see cref="GraphFormattingConfigBase.Normalize"/>.
    /// </summary>
    public static void ApplyTickDensity(ScottPlot.Plot plot, double density = DefaultTickDensity)
    {
        if (plot.Axes.Bottom.TickGenerator is ScottPlot.TickGenerators.NumericAutomatic bottomAuto)
        {
            bottomAuto.TickDensity = density;
        }
        if (plot.Axes.Left.TickGenerator is ScottPlot.TickGenerators.NumericAutomatic leftAuto)
        {
            leftAuto.TickDensity = density;
        }
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
        float tickWidth = (float)config.TickWidth;

        ConfigureTickMarkStyle(plot.Axes.Bottom.MajorTickStyle, MajorTickLengthBase, tickWidth, scale, showMajor);
        ConfigureTickMarkStyle(plot.Axes.Bottom.MinorTickStyle, MinorTickLengthBase, tickWidth, scale, showMinor);
        ConfigureTickMarkStyle(plot.Axes.Left.MajorTickStyle, MajorTickLengthBase, tickWidth, scale, showMajor && yAxisVisible);
        ConfigureTickMarkStyle(plot.Axes.Left.MinorTickStyle, MinorTickLengthBase, tickWidth, scale, showMinor && yAxisVisible);
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
    /// <see cref="GraphFormattingConfigBase.ShowGrid"/>. When the grid is
    /// visible, the major / minor grid line color is overridden from
    /// ScottPlot's stock #CCCCCC to <see cref="DefaultGridColorHex"/> so
    /// the gridlines blend into the LabPlot chrome and do not compete
    /// with the data curves. Reads <c>plot.Grid.XAxisStyle</c> /
    /// <c>YAxisStyle</c> introduced in ScottPlot 5; falls back gracefully
    /// when those style objects are not present at runtime.
    /// </summary>
    public static void ApplyGrid(ScottPlot.Plot plot, GraphFormattingConfigBase config)
    {
        if (config.ShowGrid)
        {
            plot.ShowGrid();

            var gridColor = ColorFromHex(DefaultGridColorHex, DefaultGridColorHex);
            plot.Grid.XAxisStyle.MajorLineStyle.Color = gridColor;
            plot.Grid.XAxisStyle.MinorLineStyle.Color = gridColor.WithAlpha(0.6);
            plot.Grid.YAxisStyle.MajorLineStyle.Color = gridColor;
            plot.Grid.YAxisStyle.MinorLineStyle.Color = gridColor.WithAlpha(0.6);
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
    /// ScottPlot's stock <c>Legend.Margin</c> at every edge. The legend
    /// sits this many pixels inside the data area when offset = 0; user
    /// offsets are applied as deltas relative to this baseline so a small
    /// nudge stays close to the anchor instead of jumping off-canvas.
    /// </summary>
    public const float DefaultLegendEdgeMargin = 5f;

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
            plot.Legend.Margin = ComputeLegendMargin(
                config.LegendPosition,
                config.LegendOffsetX,
                config.LegendOffsetY);

            ApplyLegendChrome(plot);
        }
    }

    /// <summary>
    /// Repaints the legend background and outline so it reads as part of
    /// the LabPlot UI chrome rather than ScottPlot's stock grey-fill /
    /// black-outline. Background is solid white, outline matches the
    /// sidebar border (<see cref="DefaultLegendOutlineColorHex"/>) at
    /// <see cref="DefaultLegendOutlineWidth"/> px. Touches
    /// <c>BackgroundFillStyle</c> and <c>OutlineStyle</c> directly because
    /// ScottPlot 5 does not expose shortcut <c>BackgroundColor</c> /
    /// <c>OutlineColor</c> properties on <c>Legend</c>.
    /// </summary>
    public static void ApplyLegendChrome(ScottPlot.Plot plot)
    {
        var background = ColorFromHex(DefaultLegendBackgroundColorHex, DefaultLegendBackgroundColorHex);
        var outline = ColorFromHex(DefaultLegendOutlineColorHex, DefaultLegendOutlineColorHex);

        plot.Legend.BackgroundFillStyle.Color = background;
        plot.Legend.OutlineStyle.Color = outline;
        plot.Legend.OutlineStyle.Width = DefaultLegendOutlineWidth;
    }

    /// <summary>
    /// Map the LabPlot legend-position string (e.g. <c>"UpperRight"</c>)
    /// onto the matching <see cref="ScottPlot.Alignment"/> enum value.
    /// Used by both <see cref="ApplyLegend"/> when applying a config and
    /// by <c>LegendDragController</c> when re-anchoring the legend on the
    /// fly during a drag. Unknown values fall back to
    /// <see cref="ScottPlot.Alignment.UpperRight"/>.
    /// </summary>
    public static ScottPlot.Alignment MapLegendAlignment(string position) => position switch
    {
        "UpperLeft" => ScottPlot.Alignment.UpperLeft,
        "UpperCenter" => ScottPlot.Alignment.UpperCenter,
        "UpperRight" => ScottPlot.Alignment.UpperRight,
        "MiddleLeft" => ScottPlot.Alignment.MiddleLeft,
        "MiddleCenter" => ScottPlot.Alignment.MiddleCenter,
        "MiddleRight" => ScottPlot.Alignment.MiddleRight,
        "LowerLeft" => ScottPlot.Alignment.LowerLeft,
        "LowerCenter" => ScottPlot.Alignment.LowerCenter,
        "LowerRight" => ScottPlot.Alignment.LowerRight,
        _ => ScottPlot.Alignment.UpperRight,
    };

    /// <summary>
    /// Pick the best 9-cell legend anchor for a legend whose center sits
    /// at <paramref name="legendCenterX"/> / <paramref name="legendCenterY"/>
    /// inside <paramref name="dataRect"/>. The data area is split into a
    /// 3 × 3 grid; the cell containing the legend center decides whether
    /// the horizontal anchor is <c>Left</c> / <c>Center</c> / <c>Right</c>
    /// and the vertical anchor is <c>Upper</c> / <c>Middle</c> / <c>Lower</c>.
    /// Auto-picking the anchor as the user drags keeps the offset values
    /// small (within a third of the data area on each axis) and avoids
    /// the giant Margin values that pushed the legend off-canvas under
    /// the previous fixed-anchor scheme.
    /// </summary>
    public static string ChooseBestLegendAnchor(
        float legendCenterX,
        float legendCenterY,
        ScottPlot.PixelRect dataRect)
    {
        float third = dataRect.Width / 3f;
        float thirdY = dataRect.Height / 3f;

        string xPart;
        if (legendCenterX < dataRect.Left + third) xPart = "Left";
        else if (legendCenterX > dataRect.Right - third) xPart = "Right";
        else xPart = "Center";

        string yPart;
        if (legendCenterY < dataRect.Top + thirdY) yPart = "Upper";
        else if (legendCenterY > dataRect.Bottom - thirdY) yPart = "Lower";
        else yPart = "Middle";

        return yPart + xPart;
    }

    /// <summary>
    /// Inverse of <see cref="ComputeLegendMargin"/>: given a desired
    /// legend top-left position and size in pixels, compute the
    /// <c>(LegendOffsetX, LegendOffsetY)</c> values that produce that
    /// placement under <paramref name="position"/>. Used by
    /// <c>LegendDragController</c> after it picks a new anchor mid-drag,
    /// so the per-anchor offsets stay small enough that ScottPlot
    /// renders the legend inside the data area.
    /// </summary>
    public static (double X, double Y) ComputeOffsetForLegendPosition(
        string position,
        float legendLeft,
        float legendTop,
        float legendWidth,
        float legendHeight,
        ScottPlot.PixelRect dataRect)
    {
        const float pad = DefaultLegendEdgeMargin;

        double dx;
        if (position.EndsWith("Right", StringComparison.Ordinal))
        {
            dx = pad - (dataRect.Right - legendLeft - legendWidth);
        }
        else if (position.EndsWith("Left", StringComparison.Ordinal))
        {
            dx = legendLeft - dataRect.Left - pad;
        }
        else
        {
            dx = legendLeft - (dataRect.Left + dataRect.Right - legendWidth) / 2f;
        }

        double dy;
        if (position.StartsWith("Upper", StringComparison.Ordinal))
        {
            dy = legendTop - dataRect.Top - pad;
        }
        else if (position.StartsWith("Lower", StringComparison.Ordinal))
        {
            dy = pad - (dataRect.Bottom - legendTop - legendHeight);
        }
        else
        {
            dy = legendTop - (dataRect.Top + dataRect.Bottom - legendHeight) / 2f;
        }

        return (dx, dy);
    }

    /// <summary>
    /// Build a <c>PixelPadding</c> that nudges the legend by
    /// <paramref name="offsetX"/> / <paramref name="offsetY"/> pixels from
    /// the anchor implied by <paramref name="position"/>. Sign convention:
    /// <c>+X</c> moves rightwards, <c>+Y</c> moves downwards (screen
    /// coordinates). On corner anchors only one horizontal and one
    /// vertical edge participate, so the offset hits a single edge as a
    /// simple add/subtract on top of <see cref="DefaultLegendEdgeMargin"/>.
    /// On center anchors the legend is symmetric on the relevant axis, so
    /// we shift both opposing edges in opposite directions to slide the
    /// midpoint while the baseline padding keeps the legend inside the
    /// figure when offset = 0.
    /// </summary>
    public static ScottPlot.PixelPadding ComputeLegendMargin(string position, double offsetX, double offsetY)
    {
        float left = DefaultLegendEdgeMargin;
        float right = DefaultLegendEdgeMargin;
        float top = DefaultLegendEdgeMargin;
        float bottom = DefaultLegendEdgeMargin;

        float dx = (float)offsetX;
        float dy = (float)offsetY;

        // Horizontal: increasing Right pushes the legend leftwards, so the
        // sign flips for right-anchored positions; left-anchored positions
        // grow Left to push rightwards. Center anchors slide both edges to
        // shift the midpoint.
        if (position.EndsWith("Right", StringComparison.Ordinal))
        {
            right -= dx;
        }
        else if (position.EndsWith("Left", StringComparison.Ordinal))
        {
            left += dx;
        }
        else
        {
            left += dx;
            right -= dx;
        }

        // Vertical: same idea on the Y axis (Bottom grows to push upwards,
        // Top grows to push downwards). Middle anchors slide both edges.
        if (position.StartsWith("Upper", StringComparison.Ordinal))
        {
            top += dy;
        }
        else if (position.StartsWith("Lower", StringComparison.Ordinal))
        {
            bottom -= dy;
        }
        else
        {
            top += dy;
            bottom -= dy;
        }

        return new ScottPlot.PixelPadding(left, right, bottom, top);
    }
}
