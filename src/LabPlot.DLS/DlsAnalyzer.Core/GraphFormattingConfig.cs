using LabPlot.Core;

namespace DlsAnalyzer.Core;

/// <summary>
/// DLS-specific formatting config. Inherits the LabPlot-wide font / frame /
/// background / line defaults from <see cref="GraphFormattingConfigBase"/>
/// and adds the DLS-only fields for axis ranges, legend behaviour and the
/// startup-default distribution kind / run index.
/// </summary>
public sealed class GraphFormattingConfig : GraphFormattingConfigBase
{
    public const double DefaultXAxisMinNm = 0.1;
    public const double DefaultXAxisMaxNm = 10000.0;
    public const double DefaultYAxisMinPercent = 0.0;
    public const double DefaultYAxisMaxPercent = 30.0;
    public const string DefaultLegendPositionValue = "UpperRight";
    public const string DefaultDistributionModeValue = "Number";

    /// <summary>
    /// X-axis (particle size) range mode. <c>null</c> or <c>"Auto"</c> uses
    /// ScottPlot's auto-scale; <c>"Manual"</c> applies <see cref="XAxisMinNm"/>
    /// and <see cref="XAxisMaxNm"/> as the fixed visible window. Any other
    /// value normalizes back to <c>null</c>.
    /// </summary>
    public string? XAxisMode { get; set; }

    public double XAxisMinNm { get; set; } = DefaultXAxisMinNm;

    public double XAxisMaxNm { get; set; } = DefaultXAxisMaxNm;

    /// <summary>
    /// Y-axis (Number/Intensity/Volume %) range mode. Same semantics as
    /// <see cref="XAxisMode"/>.
    /// </summary>
    public string? YAxisMode { get; set; }

    public double YAxisMinPercent { get; set; } = DefaultYAxisMinPercent;

    public double YAxisMaxPercent { get; set; } = DefaultYAxisMaxPercent;

    /// <summary>
    /// Legend visibility override. <c>null</c> or <c>"Auto"</c> shows the
    /// legend only when 2+ datasets are selected (Batch 3a behaviour);
    /// <c>"Always"</c> forces it on; <c>"Never"</c> hides it regardless of
    /// the selection count. Any other value normalizes back to <c>null</c>.
    /// </summary>
    public string? LegendVisibility { get; set; }

    /// <summary>
    /// Legend placement, mapped 1:1 onto <c>ScottPlot.Alignment</c>. One of
    /// <c>"UpperRight"</c>, <c>"UpperLeft"</c>, <c>"LowerRight"</c>,
    /// <c>"LowerLeft"</c>, <c>"MiddleRight"</c>. Any other value normalizes
    /// back to <see cref="DefaultLegendPositionValue"/>. Edge-anchored
    /// "outside" placements are not exposed yet because they need a
    /// different ScottPlot API path; revisit in 3b-2.
    /// </summary>
    public string LegendPosition { get; set; } = DefaultLegendPositionValue;

    /// <summary>
    /// Distribution kind shown by default when a workbook is loaded.
    /// One of <c>"Number"</c>, <c>"Intensity"</c>, <c>"Volume"</c>; any
    /// other value normalizes back to <see cref="DefaultDistributionModeValue"/>.
    /// </summary>
    public string DefaultDistributionMode { get; set; } = DefaultDistributionModeValue;

    /// <summary>
    /// Run index (0-based) used as the initial Run when a single dataset is
    /// selected. Negative values normalize back to 0; per-dataset RunCount
    /// clamping happens at use site.
    /// </summary>
    public int DefaultRunIndex { get; set; }

    public static GraphFormattingConfig CreateFactoryDefault() => new();

    public override void Normalize()
    {
        base.Normalize();

        XAxisMode = NormalizeAxisMode(XAxisMode);
        YAxisMode = NormalizeAxisMode(YAxisMode);
        LegendVisibility = NormalizeLegendVisibility(LegendVisibility);
        LegendPosition = NormalizeLegendPosition(LegendPosition);
        DefaultDistributionMode = NormalizeDistributionMode(DefaultDistributionMode);

        if (!ConfigNormalizer.IsPositive(XAxisMinNm))
        {
            XAxisMinNm = DefaultXAxisMinNm;
        }
        if (!ConfigNormalizer.IsPositive(XAxisMaxNm))
        {
            XAxisMaxNm = DefaultXAxisMaxNm;
        }
        if (XAxisMaxNm <= XAxisMinNm)
        {
            // Inverted or collapsed range -> reset both endpoints so we
            // never hand ScottPlot a zero-width log10 window.
            XAxisMinNm = DefaultXAxisMinNm;
            XAxisMaxNm = DefaultXAxisMaxNm;
        }

        if (!double.IsFinite(YAxisMinPercent))
        {
            YAxisMinPercent = DefaultYAxisMinPercent;
        }
        if (!double.IsFinite(YAxisMaxPercent))
        {
            YAxisMaxPercent = DefaultYAxisMaxPercent;
        }
        if (YAxisMaxPercent <= YAxisMinPercent)
        {
            YAxisMinPercent = DefaultYAxisMinPercent;
            YAxisMaxPercent = DefaultYAxisMaxPercent;
        }

        if (DefaultRunIndex < 0)
        {
            DefaultRunIndex = 0;
        }
    }

    private static string? NormalizeAxisMode(string? text)
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

        if (normalized.Equals("Manual", StringComparison.OrdinalIgnoreCase))
        {
            return "Manual";
        }

        return null;
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

        if (normalized.Equals("UpperRight", StringComparison.OrdinalIgnoreCase)) return "UpperRight";
        if (normalized.Equals("UpperLeft", StringComparison.OrdinalIgnoreCase)) return "UpperLeft";
        if (normalized.Equals("LowerRight", StringComparison.OrdinalIgnoreCase)) return "LowerRight";
        if (normalized.Equals("LowerLeft", StringComparison.OrdinalIgnoreCase)) return "LowerLeft";
        if (normalized.Equals("MiddleRight", StringComparison.OrdinalIgnoreCase)) return "MiddleRight";

        return DefaultLegendPositionValue;
    }

    private static string NormalizeDistributionMode(string? text)
    {
        var normalized = ConfigNormalizer.NormalizeOptionalText(text);
        if (normalized is null)
        {
            return DefaultDistributionModeValue;
        }

        if (normalized.Equals("Number", StringComparison.OrdinalIgnoreCase)) return "Number";
        if (normalized.Equals("Intensity", StringComparison.OrdinalIgnoreCase)) return "Intensity";
        if (normalized.Equals("Volume", StringComparison.OrdinalIgnoreCase)) return "Volume";

        return DefaultDistributionModeValue;
    }
}
