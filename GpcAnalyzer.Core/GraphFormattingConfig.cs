using System.Globalization;

namespace GpcAnalyzer.Core;

public sealed class GraphFormattingConfig
{
    public const double DefaultFontSize = 12;
    public const double DefaultLineWidth = 1.5;
    public const double DefaultMarkerSize = 0;
    public const double DefaultPlotFrameWidth = 1;
    public const string DefaultPlotFrameColorHex = "#475569";

    public string? FontName { get; set; }
    public double FontSize { get; set; } = DefaultFontSize;
    public bool ShowGrid { get; set; } = true;
    public bool ShowYAxisTickLabels { get; set; } = true;
    public bool ShowPlotFrame { get; set; } = true;
    public double PlotFrameWidth { get; set; } = DefaultPlotFrameWidth;
    public string PlotFrameColorHex { get; set; } = DefaultPlotFrameColorHex;
    public string? AspectRatio { get; set; }
    public string? DefaultLineColorHex { get; set; }
    public double LineWidth { get; set; } = DefaultLineWidth;
    public double MarkerSize { get; set; } = DefaultMarkerSize;

    public static GraphFormattingConfig CreateFactoryDefault()
    {
        return new GraphFormattingConfig();
    }

    public void Normalize()
    {
        FontName = NormalizeOptionalText(FontName);
        AspectRatio = NormalizeOptionalText(AspectRatio);
        DefaultLineColorHex = NormalizeOptionalHex(DefaultLineColorHex);

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
