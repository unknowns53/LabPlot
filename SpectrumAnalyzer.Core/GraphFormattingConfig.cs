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

    /// <summary>
    /// User-defined integration regions persisted alongside the formatting
    /// defaults and session files. Always integrated against each dataset's
    /// native Y values regardless of any A↔T display override.
    /// </summary>
    public IList<IntegrationRegion> IntegrationRegions { get; set; } = new List<IntegrationRegion>();

    /// <summary>
    /// Beer-Lambert calibration curve configuration. <c>null</c> until the
    /// user opens the calibration editor for the first time. The associated
    /// integration region (when in IntegrationArea mode) lives in
    /// <see cref="IntegrationRegions"/> and is referenced here by label.
    /// </summary>
    public CalibrationCurveConfig? Calibration { get; set; }

    // ----------- Wavelength scan: λmax markers -----------
    public bool ShowLambdaMaxMarkers { get; set; }

    /// <summary>
    /// Minimum absorbance for a local maximum to be flagged as λmax. Defaults
    /// to 0.05 to filter out baseline noise.
    /// </summary>
    public double LambdaMaxMinAbsorbance { get; set; } = 0.05;

    /// <summary>
    /// Maximum number of λmax markers to render per dataset. 0 means
    /// unlimited.
    /// </summary>
    public int LambdaMaxCount { get; set; } = 3;

    // ----------- Temperature scan: cloud-point detection -----------
    public bool ShowCloudPointMarkers { get; set; }

    /// <summary>
    /// Selected cloud-point estimation method. Values: <c>"Midpoint"</c>,
    /// <c>"FirstDerivativePeak"</c>, <c>"SecondDerivativeExtremum"</c>,
    /// <c>"SigmoidFit"</c>. Anything else normalises back to Midpoint.
    /// </summary>
    public string? CloudPointMethod { get; set; }

    /// <summary>
    /// Transmittance threshold (%) used by the midpoint method.
    /// </summary>
    public double CloudPointThresholdPercent { get; set; } = 50.0;

    /// <summary>
    /// When <c>true</c> and the SigmoidFit method is selected, the fitted
    /// Boltzmann curve is overlaid on the temperature scan (dashed line in
    /// the dataset's colour). Ignored for other methods.
    /// </summary>
    public bool ShowCloudPointFitCurve { get; set; } = true;

    /// <summary>
    /// When <c>true</c> and the SigmoidFit method is selected, the auxiliary
    /// fit parameters (k, R²) are appended to the result text in the analysis
    /// panel. Ignored for other methods.
    /// </summary>
    public bool ShowCloudPointFitParameters { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, the JASCO footer metadata of the active temperature
    /// scan (measurement wavelength, ramp rate, accessory, ...) is rendered
    /// as a small annotation on the plot.
    /// </summary>
    public bool ShowTemperatureScanMetadata { get; set; }

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
        IntegrationRegions = NormalizeIntegrationRegions(IntegrationRegions);
        Calibration = NormalizeCalibration(Calibration);
        CloudPointMethod = NormalizeCloudPointMethod(CloudPointMethod);

        if (!IsFiniteRange(LambdaMaxMinAbsorbance, 0.0, 100.0))
        {
            LambdaMaxMinAbsorbance = 0.05;
        }

        if (LambdaMaxCount < 0 || LambdaMaxCount > 50)
        {
            LambdaMaxCount = 3;
        }

        if (!IsFiniteRange(CloudPointThresholdPercent, 0.001, 100.0))
        {
            CloudPointThresholdPercent = 50.0;
        }

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

    private static IList<IntegrationRegion> NormalizeIntegrationRegions(IList<IntegrationRegion>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new List<IntegrationRegion>();
        }

        var result = new List<IntegrationRegion>(source.Count);
        foreach (var raw in source)
        {
            if (raw is null)
            {
                continue;
            }

            // Defensive clamp for the new method-specific parameters: hand-
            // edited config files (and old sessions that pre-date the
            // properties — which deserialize to 0) should not break loading.
            // Out-of-range values silently snap to the defaults / valid range.
            var region = raw with
            {
                RubberBandSegments = Math.Clamp(raw.RubberBandSegments <= 0 ? 16 : raw.RubberBandSegments, 2, 1024),
                PolynomialOrder = Math.Clamp(raw.PolynomialOrder <= 0 ? 2 : raw.PolynomialOrder, 1, 5),
            };

            if (!region.IsValid)
            {
                continue;
            }

            result.Add(region);
        }

        return result;
    }

    private static CalibrationCurveConfig? NormalizeCalibration(CalibrationCurveConfig? source)
    {
        if (source is null)
        {
            return null;
        }

        // Defensive clamping for hand-edited config files and old sessions
        // that pre-date the properties (deserialize to 0). The wavelength
        // window is generous so we cover UV (≥190 nm) through far-IR
        // expressed in nm; out-of-range values fall back to the default.
        if (!IsFiniteRange(source.WavelengthNm, 1.0, 1_000_000.0))
        {
            source.WavelengthNm = 280.0;
        }

        if (!IsPositive(source.PathLengthCm))
        {
            source.PathLengthCm = 1.0;
        }

        if (source.MolarMass is { } mw && !IsPositive(mw))
        {
            source.MolarMass = null;
        }

        source.IntegrationRegionLabel = NormalizeOptionalText(source.IntegrationRegionLabel);
        source.Samples = NormalizeCalibrationSamples(source.Samples);
        return source;
    }

    private static IList<CalibrationSample> NormalizeCalibrationSamples(IList<CalibrationSample>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new List<CalibrationSample>();
        }

        var result = new List<CalibrationSample>(source.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in source)
        {
            if (raw is null || string.IsNullOrWhiteSpace(raw.DatasetKey))
            {
                continue;
            }

            if (raw.ConcentrationInUnit is { } c && !double.IsFinite(c))
            {
                raw.ConcentrationInUnit = null;
            }

            // Skip duplicates so a damaged session doesn't end up with two
            // entries for the same dataset (the editor keys by DatasetKey).
            if (!seen.Add(raw.DatasetKey))
            {
                continue;
            }

            result.Add(raw);
        }

        return result;
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

    private static string? NormalizeCloudPointMethod(string? text)
    {
        var normalized = NormalizeOptionalText(text);
        if (normalized is null) return null;

        if (normalized.Equals("Midpoint", StringComparison.OrdinalIgnoreCase))
        {
            return "Midpoint";
        }

        if (normalized.Equals("FirstDerivativePeak", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Derivative", StringComparison.OrdinalIgnoreCase))
        {
            return "FirstDerivativePeak";
        }

        if (normalized.Equals("SecondDerivativeExtremum", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("SecondDerivative", StringComparison.OrdinalIgnoreCase))
        {
            return "SecondDerivativeExtremum";
        }

        if (normalized.Equals("SigmoidFit", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Sigmoid", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Boltzmann", StringComparison.OrdinalIgnoreCase))
        {
            return "SigmoidFit";
        }

        return null;
    }

    private static bool IsFiniteRange(double value, double min, double max)
    {
        return double.IsFinite(value) && value >= min && value <= max;
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
