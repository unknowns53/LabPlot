using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;

namespace GpcAnalyzer.Core;

public sealed class CsvGpcDataReader : IGpcDataReader
{
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

    public GpcDataset Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("GPC data file was not found.", filePath);
        }

        var lines = File.ReadAllLines(filePath, LenientUtf8);
        if (TryReadLabSolutionsChromatogram(filePath, lines, out var labSolutionsDataset))
        {
            return labSolutionsDataset;
        }

        return ReadDelimitedFile(filePath, lines);
    }

    private static GpcDataset ReadDelimitedFile(string filePath, IReadOnlyList<string> lines)
    {
        var headerLine = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (headerLine is null)
        {
            throw new InvalidDataException("The file is empty.");
        }

        var delimiter = GuessDelimiter(headerLine);
        return delimiter is null
            ? ReadWhitespaceDelimitedFile(filePath, lines)
            : ReadCsvFile(filePath, delimiter);
    }

    private static GpcDataset ReadCsvFile(string filePath, string delimiter)
    {
        var points = new List<GpcDataPoint>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            Delimiter = delimiter,
            HasHeaderRecord = true,
            HeaderValidated = null,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        using var stream = new StreamReader(filePath, LenientUtf8, true);
        using var csv = new CsvReader(stream, config);

        if (!csv.Read())
        {
            throw new InvalidDataException("The file does not contain a header row.");
        }

        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        var xLabel = GetLabel(headers, 0, "X");
        var yLabel = GetLabel(headers, 1, "Y");

        while (csv.Read())
        {
            var xText = TryGetField(csv, 0);
            var yText = TryGetField(csv, 1);

            if (TryParseDouble(xText, out var x) && TryParseDouble(yText, out var y))
            {
                points.Add(new GpcDataPoint { X = x, Y = y });
            }
        }

        return CreateDataset(filePath, xLabel, yLabel, points);
    }

    private static GpcDataset ReadWhitespaceDelimitedFile(string filePath, IReadOnlyList<string> lines)
    {
        var points = new List<GpcDataPoint>();
        var header = SplitLooseColumns(lines.First(line => !string.IsNullOrWhiteSpace(line)));
        var xLabel = GetLabel(header, 0, "X");
        var yLabel = GetLabel(header, 1, "Y");

        foreach (var line in lines.SkipWhile(line => string.IsNullOrWhiteSpace(line)).Skip(1))
        {
            var columns = SplitLooseColumns(line);
            if (columns.Length < 2)
            {
                continue;
            }

            if (TryParseDouble(columns[0], out var x) && TryParseDouble(columns[1], out var y))
            {
                points.Add(new GpcDataPoint { X = x, Y = y });
            }
        }

        return CreateDataset(filePath, xLabel, yLabel, points);
    }

    private static bool TryReadLabSolutionsChromatogram(
        string filePath,
        IReadOnlyList<string> lines,
        out GpcDataset dataset)
    {
        dataset = new GpcDataset();

        var detectorDatasets = new Dictionary<string, GpcDetectorDataset>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (!IsLabSolutionsChromatogramSection(trimmed))
            {
                continue;
            }

            var detector = ParseLabSolutionsDetector(trimmed) ?? $"Detector {detectorDatasets.Count + 1}";
            var points = new List<GpcDataPoint>();
            var readingPoints = false;
            var intensityMultiplier = 1.0;
            var xLabel = "R.Time (min)";
            var yLabel = "Intensity";
            var yUnits = string.Empty;

            for (i++; i < lines.Count; i++)
            {
                line = lines[i];
                trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    i--;
                    break;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                var columns = SplitLooseColumns(line);
                if (!readingPoints)
                {
                    ReadLabSolutionsMetadata(columns, ref intensityMultiplier, ref yUnits);

                    if (LooksLikeChromatogramHeader(columns))
                    {
                        xLabel = columns[0];
                        yLabel = BuildIntensityLabel(columns[1], yUnits);
                        readingPoints = true;
                    }

                    continue;
                }

                if (columns.Length < 2)
                {
                    continue;
                }

                if (TryParseDouble(columns[0], out var x) && TryParseDouble(columns[1], out var y))
                {
                    points.Add(new GpcDataPoint { X = x, Y = y * intensityMultiplier });
                }
            }

            if (points.Count > 0)
            {
                detectorDatasets[detector] = new GpcDetectorDataset
                {
                    Detector = detector,
                    XLabel = xLabel,
                    YLabel = yLabel,
                    Points = points.ToArray(),
                };
            }
        }

        if (detectorDatasets.Count == 0)
        {
            return false;
        }

        var firstDetectorDataset = detectorDatasets.Values.First();
        dataset = CreateDataset(
            filePath,
            firstDetectorDataset.XLabel,
            firstDetectorDataset.YLabel,
            firstDetectorDataset.Points,
            firstDetectorDataset.Detector,
            detectorDatasets);
        return true;
    }

    private static void ReadLabSolutionsMetadata(
        IReadOnlyList<string> columns,
        ref double intensityMultiplier,
        ref string yUnits)
    {
        if (columns.Count < 2)
        {
            return;
        }

        if (columns[0].Equals("Intensity Multiplier", StringComparison.OrdinalIgnoreCase)
            && TryParseDouble(columns[1], out var multiplier))
        {
            intensityMultiplier = multiplier;
        }

        if (columns[0].Equals("Intensity Units", StringComparison.OrdinalIgnoreCase))
        {
            yUnits = columns[1];
        }
    }

    private static GpcDataset CreateDataset(
        string filePath,
        string xLabel,
        string yLabel,
        IReadOnlyList<GpcDataPoint> points,
        string? detector = null,
        IReadOnlyDictionary<string, GpcDetectorDataset>? detectorDatasets = null)
    {
        if (points.Count == 0)
        {
            throw new InvalidDataException("No valid numeric data rows were found.");
        }

        return new GpcDataset
        {
            SourceFilePath = filePath,
            Detector = detector,
            XLabel = xLabel,
            YLabel = yLabel,
            Points = points.ToArray(),
            DetectorDatasets = detectorDatasets
                ?? new Dictionary<string, GpcDetectorDataset>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string? GuessDelimiter(string headerLine)
    {
        var candidates = new[] { ",", "\t", ";" };
        return candidates
            .Select(delimiter => new { Delimiter = delimiter, Count = headerLine.Split(delimiter).Length })
            .Where(candidate => candidate.Count >= 2)
            .OrderByDescending(candidate => candidate.Count)
            .Select(candidate => candidate.Delimiter)
            .FirstOrDefault();
    }

    private static string[] SplitLooseColumns(string line)
    {
        if (line.Contains('\t', StringComparison.Ordinal))
        {
            return line.Split('\t', StringSplitOptions.TrimEntries);
        }

        if (line.Contains(',', StringComparison.Ordinal))
        {
            return line.Split(',', StringSplitOptions.TrimEntries);
        }

        if (line.Contains(';', StringComparison.Ordinal))
        {
            return line.Split(';', StringSplitOptions.TrimEntries);
        }

        return Regex.Split(line.Trim(), @"\s+");
    }

    private static bool LooksLikeChromatogramHeader(IReadOnlyList<string> columns)
    {
        if (columns.Count < 2)
        {
            return false;
        }

        return Contains(columns[0], "time")
            && (Contains(columns[1], "intensity") || Contains(columns[1], "signal"));
    }

    private static bool IsLabSolutionsChromatogramSection(string line)
    {
        return line.StartsWith("[LC Chromatogram(", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ParseLabSolutionsDetector(string sectionHeader)
    {
        var match = Regex.Match(sectionHeader, @"Detector\s+([^-)\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool Contains(string value, string text)
    {
        return value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildIntensityLabel(string label, string units)
    {
        return string.IsNullOrWhiteSpace(units) ? label : $"{label} ({units})";
    }

    private static string GetLabel(IReadOnlyList<string> labels, int index, string fallback)
    {
        return labels.Count > index && !string.IsNullOrWhiteSpace(labels[index])
            ? labels[index]
            : fallback;
    }

    private static string? TryGetField(CsvReader csv, int index)
    {
        try
        {
            return csv.GetField(index);
        }
        catch (CsvHelperException)
        {
            return null;
        }
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
    }
}
