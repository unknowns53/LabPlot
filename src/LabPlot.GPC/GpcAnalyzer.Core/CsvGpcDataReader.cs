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

        using var stream = new StreamReader(filePath, LenientUtf8, true);
        var headerLine = ReadFirstNonBlankLine(stream);
        if (headerLine is null)
        {
            throw new InvalidDataException("The file is empty.");
        }

        if (CouldContainLabSolutionsSections(headerLine))
        {
            var lines = ReadRemainingLines(headerLine, stream);
            if (TryReadLabSolutionsChromatogram(filePath, lines, out var labSolutionsDataset))
            {
                return labSolutionsDataset;
            }

            return ReadDelimitedFile(filePath, lines);
        }

        var delimiter = GuessDelimiter(headerLine);
        if (delimiter is null)
        {
            return ReadWhitespaceDelimitedFile(filePath, headerLine, stream);
        }

        Rewind(stream);
        return ReadCsvFile(filePath, stream, delimiter);
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
            : ReadCsvFile(filePath, new StringReader(string.Join(Environment.NewLine, lines)), delimiter);
    }

    private static string? ReadFirstNonBlankLine(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadRemainingLines(string firstLine, TextReader reader)
    {
        var lines = new List<string> { firstLine };
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static void Rewind(StreamReader reader)
    {
        reader.DiscardBufferedData();
        reader.BaseStream.Seek(0, SeekOrigin.Begin);
    }

    private static bool CouldContainLabSolutionsSections(string headerLine)
    {
        return headerLine.TrimStart().StartsWith("[", StringComparison.Ordinal);
    }

    private static GpcDataset ReadCsvFile(string filePath, TextReader reader, string delimiter)
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

        using var csv = new CsvReader(reader, config);

        if (!csv.Read())
        {
            throw new InvalidDataException("The file does not contain a header row.");
        }

        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        var xLabel = DefaultLabels.ApplySourceOverride(GetLabel(headers, 0, "X"));
        var yLabel = DefaultLabels.ApplySourceOverride(GetLabel(headers, 1, "Y"));

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
        var xLabel = DefaultLabels.ApplySourceOverride(GetLabel(header, 0, "X"));
        var yLabel = DefaultLabels.ApplySourceOverride(GetLabel(header, 1, "Y"));

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

    private static GpcDataset ReadWhitespaceDelimitedFile(string filePath, string headerLine, TextReader reader)
    {
        var points = new List<GpcDataPoint>();
        var header = SplitLooseColumns(headerLine);
        var xLabel = DefaultLabels.ApplySourceOverride(GetLabel(header, 0, "X"));
        var yLabel = DefaultLabels.ApplySourceOverride(GetLabel(header, 1, "Y"));

        while (reader.ReadLine() is { } line)
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
        var molecularWeightStatistics = new Dictionary<string, MolecularWeightStatistics>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (IsLabSolutionsMolecularWeightTableSection(trimmed))
            {
                ReadLabSolutionsMolecularWeightStatisticsSection(
                    lines,
                    ref i,
                    trimmed,
                    molecularWeightStatistics,
                    molecularWeightStatistics.Count + 1);
                continue;
            }

            if (IsLabSolutionsChromatogramSection(trimmed))
            {
                ReadLabSolutionsChromatogramSection(
                    lines,
                    ref i,
                    trimmed,
                    detectorDatasets,
                    detectorDatasets.Count + 1);
            }
        }

        ApplyLabSolutionsStatistics(detectorDatasets, molecularWeightStatistics);
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
            firstDetectorDataset.MolecularWeightStatistics,
            detectorDatasets);
        return true;
    }

    private static void ReadLabSolutionsChromatogramSection(
        IReadOnlyList<string> lines,
        ref int index,
        string sectionHeader,
        IDictionary<string, GpcDetectorDataset> detectorDatasets,
        int fallbackDetectorIndex)
    {
        var detector = ParseLabSolutionsDetector(sectionHeader) ?? $"Detector {fallbackDetectorIndex}";
        var points = new List<GpcDataPoint>();
        var readingPoints = false;
        var intensityMultiplier = 1.0;
        var xLabel = "R.Time (min)";
        var yLabel = "Intensity";
        var yUnits = string.Empty;

        for (index++; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                index--;
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
                    xLabel = DefaultLabels.ApplySourceOverride(columns[0]);
                    // 単位が CSV から取れているときは override を経由せずそのまま
                    // "{label} ({unit})" 形式にする。override 経由だと
                    // "Intensity" → "Intensity [mV]" のように単位が括弧付きで
                    // 注入された後さらに "(mV)" が付与されて二重表記になる。
                    yLabel = string.IsNullOrWhiteSpace(yUnits)
                        ? DefaultLabels.ApplySourceOverride(columns[1])
                        : BuildIntensityLabel(columns[1], yUnits);
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

    private static void ApplyLabSolutionsStatistics(
        IDictionary<string, GpcDetectorDataset> detectorDatasets,
        IReadOnlyDictionary<string, MolecularWeightStatistics> molecularWeightStatistics)
    {
        foreach (var detector in detectorDatasets.Keys.ToArray())
        {
            if (!molecularWeightStatistics.TryGetValue(detector, out var statistics))
            {
                continue;
            }

            var detectorDataset = detectorDatasets[detector];
            detectorDatasets[detector] = new GpcDetectorDataset
            {
                Detector = detectorDataset.Detector,
                XLabel = detectorDataset.XLabel,
                YLabel = detectorDataset.YLabel,
                MolecularWeightStatistics = statistics,
                Points = detectorDataset.Points,
            };
        }
    }

    private static void ReadLabSolutionsMolecularWeightStatisticsSection(
        IReadOnlyList<string> lines,
        ref int index,
        string sectionHeader,
        IDictionary<string, MolecularWeightStatistics> statisticsByDetector,
        int fallbackDetectorIndex)
    {
        var detector = ParseLabSolutionsDetector(sectionHeader) ?? $"Detector {fallbackDetectorIndex}";
        string[]? headers = null;
        var peaks = new List<MolecularWeightPeak>();

        for (index++; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                index--;
                break;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var columns = SplitLooseColumns(lines[index]);
            if (headers is null && columns.Length > 1 && columns[0].Equals("Peak#", StringComparison.OrdinalIgnoreCase))
            {
                headers = columns;
                continue;
            }

            if (headers is null || columns.Length == 0 || columns[0].Equals("Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var peak = CreateMolecularWeightPeakFromRow(headers, columns);
            if (peak is not null && peak.HasAnyValue)
            {
                peaks.Add(peak);
            }
        }

        var statistics = CreateMolecularWeightStatisticsFromPeaks(peaks);
        if (statistics is not null && statistics.HasAnyValue)
        {
            statisticsByDetector[detector] = statistics;
        }
    }

    private static MolecularWeightStatistics? CreateMolecularWeightStatisticsFromPeaks(
        IReadOnlyList<MolecularWeightPeak> peaks)
    {
        var orderedPeaks = peaks
            .OrderByDescending(peak => peak.Percent ?? double.NegativeInfinity)
            .ToArray();
        var representativePeak = MolecularWeightStatistics.SelectAutoRepresentativePeak(orderedPeaks);
        if (representativePeak is null)
        {
            return null;
        }

        return new MolecularWeightStatistics
        {
            Mn = representativePeak.Mn,
            Mw = representativePeak.Mw,
            Pdi = representativePeak.Pdi,
            Source = MolecularWeightStatisticsSource.DataFile,
            Peaks = orderedPeaks,
            SelectedPeakId = null,
        };
    }

    private static MolecularWeightPeak? CreateMolecularWeightPeakFromRow(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> values)
    {
        var mn = GetNumericColumn(headers, values, "Mn");
        var mw = GetNumericColumn(headers, values, "Mw");
        var pdi = GetNumericColumn(headers, values, "Mw/Mn");
        if (!IsPositive(mn) || !IsPositive(mw))
        {
            return null;
        }

        if (!pdi.HasValue
            && mn.HasValue
            && mw.HasValue
            && double.IsFinite(mn.Value)
            && double.IsFinite(mw.Value)
            && Math.Abs(mn.Value) > double.Epsilon)
        {
            pdi = mw.Value / mn.Value;
        }

        var peakId = values.Count > 0 ? values[0] : string.Empty;
        if (string.IsNullOrWhiteSpace(peakId))
        {
            return null;
        }

        return new MolecularWeightPeak
        {
            PeakId = peakId,
            Mn = mn,
            Mw = mw,
            Pdi = pdi,
            Percent = GetNumericColumn(headers, values, "%"),
        };
    }

    private static bool IsPositive(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value) && value.Value > 0;
    }

    private static double? GetNumericColumn(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> values,
        string name)
    {
        var index = -1;
        for (var i = 0; i < headers.Count; i++)
        {
            if (headers[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || index >= values.Count)
        {
            return null;
        }

        return TryParseDouble(values[index], out var value) && double.IsFinite(value)
            ? value
            : null;
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
        MolecularWeightStatistics? molecularWeightStatistics = null,
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
            MolecularWeightStatistics = molecularWeightStatistics,
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

    private static bool IsLabSolutionsMolecularWeightTableSection(string line)
    {
        return line.StartsWith("[Average Molecular Weight Table(", StringComparison.OrdinalIgnoreCase);
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
