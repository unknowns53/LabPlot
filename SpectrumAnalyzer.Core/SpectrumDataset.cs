namespace SpectrumAnalyzer.Core;

public sealed class SpectrumDataset
{
    private double[]? _xValues;
    private double[]? _yValues;

    public string? SourceFilePath { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

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
