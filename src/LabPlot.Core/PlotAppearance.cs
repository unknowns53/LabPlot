namespace LabPlot.Core;

/// <summary>
/// WPF-independent ScottPlot helpers shared by every LabPlot app. Each
/// app's <c>ApplyPlotAppearance</c> path threads UI control state into
/// these helpers; the helpers themselves only touch the
/// <see cref="ScottPlot.Plot"/> object so they can be reused from a
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
}
