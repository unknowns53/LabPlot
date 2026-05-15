using System.Globalization;
using System.Text;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Reads JASCO Spectra Manager text exports (UV-Vis V-series TXT and FTIR CSV).
/// </summary>
/// <remarks>
/// File layout: header rows of "KEY{sep}VALUE" pairs (TITLE, DATA TYPE, ORIGIN,
/// XUNITS, YUNITS, FIRSTX, LASTX, NPOINTS, DELTAX, ...) followed by an "XYDATA"
/// marker line, then two-column numeric rows. After the data block a blank line
/// separates the Shift-JIS metadata footer with `[測定情報]` / `[付属品情報]`
/// sections; the parser walks through that footer too and surfaces the
/// key/value pairs via <see cref="SpectrumDataset.Metadata"/>. The separator is
/// tab for V-series TXT exports and comma for FTIR CSV exports; both are
/// auto-detected per row.
/// </remarks>
public sealed class JascoSpectrumReader : ISpectrumDataReader
{
    private static readonly Encoding ShiftJis = ResolveShiftJisEncoding();

    private static readonly char[] FieldSeparators = { '\t', ',' };

    private enum ParserState
    {
        Header,
        Data,
        Footer,
    }

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

        using var reader = new StreamReader(filePath, ShiftJis, detectEncodingFromByteOrderMarks: true);
        return Parse(reader, filePath);
    }

    public SpectrumDataset Parse(TextReader reader, string? sourceFilePath)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var points = new List<SpectrumDataPoint>();
        var state = ParserState.Header;

        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.TrimEnd();

            switch (state)
            {
                case ParserState.Header:
                    if (line.Equals("XYDATA", StringComparison.OrdinalIgnoreCase))
                    {
                        state = ParserState.Data;
                        continue;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    TryAddPair(line, header, StringComparer.OrdinalIgnoreCase);
                    break;

                case ParserState.Data:
                    if (line.Length == 0)
                    {
                        state = ParserState.Footer;
                        continue;
                    }

                    if (TryParseDataRow(line, out var point))
                    {
                        points.Add(point);
                    }
                    break;

                case ParserState.Footer:
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    // Section markers like "[測定情報]" delimit footer
                    // groups but don't carry their own value. Skip them.
                    if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
                    {
                        continue;
                    }

                    TryAddPair(line, metadata, StringComparer.Ordinal);
                    break;
            }
        }

        if (state == ParserState.Header)
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
        var firstX = TryParseHeaderDouble(header, "FIRSTX");
        var lastX = TryParseHeaderDouble(header, "LASTX");

        return new SpectrumDataset
        {
            SourceFilePath = sourceFilePath,
            RawXUnits = xUnits,
            RawYUnits = yUnits,
            RawDataType = dataType,
            RawFirstX = firstX,
            RawLastX = lastX,
            XLabel = DefaultLabels.ApplySourceOverride(AxisLabelMapper.MapX(xUnits)),
            YLabel = DefaultLabels.ApplySourceOverride(AxisLabelMapper.MapY(yUnits)),
            Title = title,
            Metadata = metadata,
            Points = points,
        };
    }

    private static void TryAddPair(
        string line,
        IDictionary<string, string> sink,
        StringComparer keyComparer)
    {
        var separatorIndex = line.IndexOfAny(FieldSeparators);
        if (separatorIndex < 0) return;

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        if (key.Length == 0) return;

        // Footer entries with empty values (e.g. an unfilled `タイトル` slot
        // in the comment section) shouldn't shadow real data — keep them
        // out of the dictionary entirely.
        if (ReferenceEquals(keyComparer, StringComparer.Ordinal) && value.Length == 0)
        {
            return;
        }

        sink[key] = value;
    }

    private static Encoding ResolveShiftJisEncoding()
    {
        // .NET (Core) ships only ASCII / UTF / Latin-1 by default. Register
        // the code-page provider so Shift-JIS is available without the
        // caller having to do it.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding("shift_jis");
        }
        catch (ArgumentException)
        {
            // Extreme fallback: a pure-ASCII reader. Loses the Japanese
            // footer but keeps the data readable on platforms where the
            // code page is genuinely unavailable.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }
    }

    private static double? TryParseHeaderDouble(IReadOnlyDictionary<string, string> header, string key)
    {
        if (!header.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return TryParseLooseDouble(raw, out var value) ? value : null;
    }

    private static bool TryParseDataRow(string line, out SpectrumDataPoint point)
    {
        // Auto-detect the separator per row instead of splitting on both tab
        // and comma at once. The shared "{tab, comma}" approach corrupts
        // decimal-comma exports such as "0,5\t1,234" by breaking the values
        // at the decimal point. TXT exports use tab, CSV exports use comma,
        // so picking whichever is present in the row keeps both happy.
        var separator = line.IndexOf('\t') >= 0 ? '\t' : ',';
        var fields = line.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            point = default;
            return false;
        }

        if (!TryParseLooseDouble(fields[0], out var x)
            || !TryParseLooseDouble(fields[1], out var y))
        {
            point = default;
            return false;
        }

        point = new SpectrumDataPoint { X = x, Y = y };
        return true;
    }

    // double.TryParse("NaN", ...) succeeds, so a downstream IsFinite gate
    // is required to keep NaN / ±Infinity out of the dataset. We also fall
    // back to a "comma → dot" swap so European-style decimal commas
    // ("0,5") parse correctly under InvariantCulture without dragging in
    // AllowThousands (which would silently strip commas from "0,00833").
    private static bool TryParseLooseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value))
        {
            return true;
        }

        if (text.IndexOf(',') >= 0 && text.IndexOf('.') < 0)
        {
            var swapped = text.Replace(',', '.');
            if (double.TryParse(swapped, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && double.IsFinite(value))
            {
                return true;
            }
        }

        value = 0;
        return false;
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
            // Distinguish Kelvin exports from Celsius — collapsing both
            // to "Temperature / °C" would silently mis-label a K trace
            // and downstream temperature ramps would interpret K values
            // as Celsius (a 273 K offset on every point).
            if (normalized.Contains("[K]", StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("Kelvin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Temperature / K";
            }
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
