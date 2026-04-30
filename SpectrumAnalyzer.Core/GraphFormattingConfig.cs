using System.Globalization;

namespace SpectrumAnalyzer.Core;

public sealed class GraphFormattingConfig
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
    /// User-controlled override for the X-axis direction.
    /// </summary>
    /// <remarks>
    /// <c>null</c> or <c>"Auto"</c> means follow the dataset (IR data is inverted,
    /// UV-Vis stays in normal direction). <c>"Inverted"</c> always inverts the X
    /// axis, <c>"Normal"</c> always keeps it in normal direction. Any other value
    /// is normalized back to <c>null</c>.
    /// </remarks>
    public string? InvertXAxisMode { get; set; }

    /// <summary>
    /// User-controlled Y axis display mode.
    /// </summary>
    /// <remarks>
    /// <c>null</c> or <c>"Native"</c> uses the YUNITS recorded in the source
    /// file. <c>"Absorbance"</c> / <c>"Transmittance"</c> force the corresponding
    /// representation, converting the data on the fly when the source is the
    /// other unit. Datasets whose YUNITS is neither (Reflectance, Temperature,
    /// ...) stay in their native units regardless of the override.
    /// </remarks>
    public string? YAxisDisplayMode { get; set; }

    /// <summary>
    /// Labels of the IR peak assignments the user has enabled in the format
    /// panel. Matched against <see cref="PeakAssignment.Label"/> when the
    /// active dataset is an IR spectrum. Other dataset types ignore this list.
    /// </summary>
    public IList<string> EnabledIrPeakAssignmentLabels { get; set; } = new List<string>();

    // User preferences (persisted alongside the formatting defaults).
    public string? DefaultOutputDirectory { get; set; }

    public static GraphFormattingConfig CreateFactoryDefault()
    {
        return new GraphFormattingConfig();
    }

    public void Normalize()
    {
        FontName = NormalizeOptionalText(FontName);
        AspectRatio = NormalizeOptionalText(AspectRatio);
        DefaultLineColorHex = NormalizeOptionalHex(DefaultLineColorHex);
        DefaultOutputDirectory = NormalizeOptionalText(DefaultOutputDirectory);
        InvertXAxisMode = NormalizeInvertXAxisMode(InvertXAxisMode);
        YAxisDisplayMode = NormalizeYAxisDisplayMode(YAxisDisplayMode);
        EnabledIrPeakAssignmentLabels = NormalizeEnabledLabels(EnabledIrPeakAssignmentLabels);

        if (!IsPositive(FontSize))
        {
            FontSize = DefaultFontSize;
        }

        if (!IsPositive(PlotFrameWidth))
        {
            PlotFrameWidth = DefaultPlotFrameWidth;
        }

        if (!IsHexColor(PlotFrameColorHex))
        {
            PlotFrameColorHex = DefaultPlotFrameColorHex;
        }

        if (!IsHexColor(BackgroundColorHex))
        {
            BackgroundColorHex = DefaultBackgroundColorHex;
        }

        if (!IsPositive(LineWidth))
        {
            LineWidth = DefaultLineWidth;
        }

        if (!IsNonNegative(MarkerSize))
        {
            MarkerSize = DefaultMarkerSize;
        }
    }

    public string FormatFontSize()
    {
        return FormatNumber(FontSize);
    }

    public string FormatFrameWidth()
    {
        return FormatNumber(PlotFrameWidth);
    }

    public string FormatLineWidth()
    {
        return FormatNumber(LineWidth);
    }

    public string FormatMarkerSize()
    {
        return FormatNumber(MarkerSize);
    }

    private static string? NormalizeOptionalText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? NormalizeOptionalHex(string? text)
    {
        var normalized = NormalizeOptionalText(text);
        return normalized is not null && IsHexColor(normalized) ? normalized : null;
    }

    private static string? NormalizeInvertXAxisMode(string? text)
    {
        var normalized = NormalizeOptionalText(text);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (normalized.Equals("Inverted", StringComparison.OrdinalIgnoreCase))
        {
            return "Inverted";
        }

        if (normalized.Equals("Normal", StringComparison.OrdinalIgnoreCase))
        {
            return "Normal";
        }

        return null;
    }

    private static IList<string> NormalizeEnabledLabels(IList<string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new List<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(source.Count);
        foreach (var label in source)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var trimmed = label.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static string? NormalizeYAxisDisplayMode(string? text)
    {
        var normalized = NormalizeOptionalText(text);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Equals("Native", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (normalized.Equals("Absorbance", StringComparison.OrdinalIgnoreCase))
        {
            return "Absorbance";
        }

        if (normalized.Equals("Transmittance", StringComparison.OrdinalIgnoreCase))
        {
            return "Transmittance";
        }

        return null;
    }

    private static bool IsPositive(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private static bool IsNonNegative(double value)
    {
        return double.IsFinite(value) && value >= 0;
    }

    private static bool IsHexColor(string? value)
    {
        return value is { Length: 7 }
            && value[0] == '#'
            && value[1..].All(Uri.IsHexDigit);
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
