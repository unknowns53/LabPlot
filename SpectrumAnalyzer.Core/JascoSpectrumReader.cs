using System.Globalization;
using System.Text;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Reads JASCO Spectra Manager text exports (UV-Vis V-series TXT and FTIR CSV).
/// </summary>
/// <remarks>
/// File layout: header rows of "KEY{sep}VALUE" pairs (TITLE, DATA TYPE, ORIGIN,
/// XUNITS, YUNITS, FIRSTX, LASTX, NPOINTS, DELTAX, ...) followed by an "XYDATA"
/// marker line, then two-column numeric rows until a blank line. Anything after
/// the blank line (Shift-JIS metadata footer with sample / instrument settings)
/// is ignored. The separator is tab for V-series TXT exports and comma for FTIR
/// CSV exports; both are auto-detected per row.
/// </remarks>
public sealed class JascoSpectrumReader : ISpectrumDataReader
{
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

    private static readonly char[] FieldSeparators = { '\t', ',' };

    public SpectrumDataset Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Spectrum data file was not found.", filePath);
        }

        using var reader = new StreamReader(filePath, LenientUtf8, true);
        return Parse(reader, filePath);
    }

    public SpectrumDataset Parse(TextReader reader, string? sourceFilePath)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var points = new List<SpectrumDataPoint>();
        var inDataSection = false;

        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.TrimEnd();
            if (!inDataSection)
            {
                if (line.Equals("XYDATA", StringComparison.OrdinalIgnoreCase))
                {
                    inDataSection = true;
                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var separatorIndex = line.IndexOfAny(FieldSeparators);
                if (separatorIndex < 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                if (key.Length > 0)
                {
                    header[key] = value;
                }
            }
            else
            {
                if (line.Length == 0)
                {
                    break;
                }

                if (TryParseDataRow(line, out var point))
                {
                    points.Add(point);
                }
            }
        }

        if (!inDataSection)
        {
            throw new InvalidDataException("XYDATA marker not found - is this a JASCO ASCII export?");
        }

        if (points.Count == 0)
        {
            throw new InvalidDataException("No numeric data rows were found below XYDATA.");
        }

        points.Sort(static (a, b) => a.X.CompareTo(b.X));

        var xUnits = header.TryGetValue("XUNITS", out var xu) ? xu : null;
        var yUnits = header.TryGetValue("YUNITS", out var yu) ? yu : null;
        var title = header.TryGetValue("TITLE", out var t) && t.Length > 0 ? t : null;
        var dataType = header.TryGetValue("DATA TYPE", out var dt) && dt.Length > 0 ? dt : null;

        return new SpectrumDataset
        {
            SourceFilePath = sourceFilePath,
            RawXUnits = xUnits,
            RawYUnits = yUnits,
            RawDataType = dataType,
            XLabel = AxisLabelMapper.MapX(xUnits),
            YLabel = AxisLabelMapper.MapY(yUnits),
            Title = title,
            Points = points,
        };
    }

    private static bool TryParseDataRow(string line, out SpectrumDataPoint point)
    {
        var fields = line.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            point = default;
            return false;
        }

        if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            point = default;
            return false;
        }

        point = new SpectrumDataPoint { X = x, Y = y };
        return true;
    }
}

internal static class AxisLabelMapper
{
    public static string MapX(string? rawUnits)
    {
        if (string.IsNullOrWhiteSpace(rawUnits))
        {
            return "X";
        }

        var normalized = rawUnits.Trim();
        if (normalized.Equals("NANOMETERS", StringComparison.OrdinalIgnoreCase))
        {
            return "Wavelength / nm";
        }

        if (normalized.StartsWith("Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return "Temperature / °C";
        }

        if (normalized.Equals("WAVENUMBERS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("CM-1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("1/CM", StringComparison.OrdinalIgnoreCase))
        {
            return "Wavenumber / cm⁻¹";
        }

        return normalized;
    }

    public static string MapY(string? rawUnits)
    {
        if (string.IsNullOrWhiteSpace(rawUnits))
        {
            return "Y";
        }

        var normalized = rawUnits.Trim();
        if (normalized.Equals("ABSORBANCE", StringComparison.OrdinalIgnoreCase))
        {
            return "Absorbance";
        }

        if (normalized.Equals("TRANSMITTANCE", StringComparison.OrdinalIgnoreCase))
        {
            return "Transmittance / %";
        }

        if (normalized.Equals("REFLECTANCE", StringComparison.OrdinalIgnoreCase))
        {
            return "Reflectance / %";
        }

        return normalized;
    }
}
