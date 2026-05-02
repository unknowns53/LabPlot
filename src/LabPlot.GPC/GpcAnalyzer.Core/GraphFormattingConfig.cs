using LabPlot.Core;

namespace GpcAnalyzer.Core;

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

    // User preferences (persisted alongside the formatting defaults).
    public string? DefaultCalibrationFilePath { get; set; }
    public string? DefaultOutputDirectory { get; set; }

    public static GraphFormattingConfig CreateFactoryDefault()
    {
        return new GraphFormattingConfig();
    }

    public void Normalize()
    {
        FontName = ConfigNormalizer.NormalizeOptionalText(FontName);
        AspectRatio = ConfigNormalizer.NormalizeOptionalText(AspectRatio);
        DefaultLineColorHex = ConfigNormalizer.NormalizeOptionalHex(DefaultLineColorHex);
        DefaultCalibrationFilePath = ConfigNormalizer.NormalizeOptionalText(DefaultCalibrationFilePath);
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
