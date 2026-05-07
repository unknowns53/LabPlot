namespace SpectrumAnalyzer.Core;

public sealed class SpectrumDataset
{
    private double[]? _xValues;
    private double[]? _yValues;

    public string? SourceFilePath { get; init; }

    public string XLabel { get; init; } = DefaultLabels.DatasetXLabel;

    public string YLabel { get; init; } = DefaultLabels.DatasetYLabel;

    public string? RawXUnits { get; init; }

    public string? RawYUnits { get; init; }

    public string? RawDataType { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// First X value as recorded in the source file's header (FIRSTX). Used to
    /// recover the original scan direction even after points have been sorted
    /// ascending for plotting / analysis.
    /// </summary>
    public double? RawFirstX { get; init; }

    /// <summary>
    /// Last X value as recorded in the source file's header (LASTX).
    /// </summary>
    public double? RawLastX { get; init; }

    /// <summary>
    /// Free-form key/value pairs recovered from the source file's footer
    /// (e.g. JASCO's `[測定情報]` and `[付属品情報]` sections). Keys are kept
    /// in their original language so that callers don't need to know an
    /// internal vocabulary; convenience accessors below pull out the
    /// commonly used fields.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<SpectrumDataPoint> Points { get; init; } = Array.Empty<SpectrumDataPoint>();

    public double[] XValues => _xValues ??= Points.Select(point => point.X).ToArray();

    public double[] YValues => _yValues ??= Points.Select(point => point.Y).ToArray();

    // X axis is in wavenumbers (cm⁻¹), so plotting convention is to display
    // the axis right-to-left (high wavenumbers on the left).
    public bool IsWavenumberAxis =>
        !string.IsNullOrWhiteSpace(RawXUnits)
        && (RawXUnits.Equals("1/CM", StringComparison.OrdinalIgnoreCase)
            || RawXUnits.Equals("CM-1", StringComparison.OrdinalIgnoreCase)
            || RawXUnits.Equals("WAVENUMBERS", StringComparison.OrdinalIgnoreCase));

    public bool IsInfraredSpectrum =>
        IsWavenumberAxis
        || string.Equals(RawDataType, "INFRARED SPECTRUM", StringComparison.OrdinalIgnoreCase);

    public bool IsAbsorbanceY =>
        string.Equals(RawYUnits, "ABSORBANCE", StringComparison.OrdinalIgnoreCase);

    public bool IsTransmittanceY =>
        string.Equals(RawYUnits, "TRANSMITTANCE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the X axis is a temperature sweep (XUNITS starts with
    /// "Temperature", e.g. "Temperature[C]" exported by JASCO V-series).
    /// </summary>
    public bool IsTemperatureScan =>
        !string.IsNullOrWhiteSpace(RawXUnits)
        && RawXUnits.StartsWith("TEMPERATURE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the X axis is a wavelength sweep (XUNITS = "NANOMETERS").
    /// </summary>
    public bool IsWavelengthScan =>
        !string.IsNullOrWhiteSpace(RawXUnits)
        && RawXUnits.Equals("NANOMETERS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Measurement wavelength reported by the JASCO footer (`測定情報` →
    /// `測定波長`, e.g. "500 nm"). Returned as the raw string so the unit
    /// suffix is preserved verbatim. Null when the field is absent.
    /// </summary>
    public string? MeasurementWavelengthText => GetMetadata("測定波長");

    /// <summary>
    /// Temperature ramp rate reported by the JASCO footer (`付属品情報` →
    /// `温度勾配`, e.g. "1 C/min"). Null when the scan was not run with a
    /// temperature accessory.
    /// </summary>
    public string? TemperatureRampRateText => GetMetadata("温度勾配");

    /// <summary>
    /// Accessory model name reported by the JASCO footer (`付属品情報` →
    /// `付属品名`, e.g. "ETC-505"). Null when no accessory is recorded.
    /// </summary>
    /// <remarks>
    /// JASCO V-series writes the key as `付属品名` (with 名), not `付属品`.
    /// Fall back to the shorter form just in case some older firmware
    /// drops the suffix.
    /// </remarks>
    public string? AccessoryName => GetMetadata("付属品名") ?? GetMetadata("付属品");

    /// <summary>
    /// Photometric mode reported by the JASCO footer (`測定情報` →
    /// `測光モード`, e.g. "%T", "Abs"). Useful for distinguishing manual
    /// %T sweeps from absorbance sweeps when YUNITS alone is ambiguous.
    /// </summary>
    public string? PhotometricMode => GetMetadata("測光モード");

    /// <summary>
    /// Spectrometer band-pass reported by the JASCO footer (`測定情報` →
    /// `UV/Vis バンド幅`, e.g. "2 nm"). Useful when comparing scans at
    /// different resolution settings.
    /// </summary>
    public string? BandPassText => GetMetadata("UV/Vis バンド幅");

    private string? GetMetadata(string key)
    {
        return Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    /// <summary>
    /// Recovers the scan direction from the source file's FIRSTX / LASTX
    /// header values. Returns <see cref="ScanDirection.Heating"/> when X
    /// originally went low → high (or wavelength scan low → high),
    /// <see cref="ScanDirection.Cooling"/> when high → low, and
    /// <see cref="ScanDirection.Unknown"/> when the headers are missing.
    /// </summary>
    public ScanDirection OriginalScanDirection
    {
        get
        {
            if (!RawFirstX.HasValue || !RawLastX.HasValue
                || !double.IsFinite(RawFirstX.Value) || !double.IsFinite(RawLastX.Value))
            {
                return ScanDirection.Unknown;
            }

            if (RawLastX.Value > RawFirstX.Value) return ScanDirection.Heating;
            if (RawLastX.Value < RawFirstX.Value) return ScanDirection.Cooling;
            return ScanDirection.Unknown;
        }
    }
}

/// <summary>
/// The direction in which a temperature sweep was acquired. The label is
/// reused for wavelength sweeps too (Heating == X ascending, Cooling == X
/// descending) when needed for hysteresis-style analyses.
/// </summary>
public enum ScanDirection
{
    Unknown = 0,
    Heating = 1,
    Cooling = 2,
}
