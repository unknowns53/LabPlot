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
}
