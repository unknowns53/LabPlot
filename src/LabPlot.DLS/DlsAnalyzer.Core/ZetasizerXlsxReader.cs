using ClosedXML.Excel;

namespace DlsAnalyzer.Core;

/// <summary>
/// Reads Zetasizer-style xlsx workbooks. One worksheet maps to one
/// <see cref="DlsDataset"/>; columns are paired (X, Y) and grouped by
/// header keyword into Number / Intensity / Volume distributions and
/// the optional g₂-1 correlation block.
/// </summary>
public sealed class ZetasizerXlsxReader : IDlsDataReader
{
    public IReadOnlyList<DlsDataset> Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is empty.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Zetasizer xlsx file not found.", filePath);

        // Open with FileShare.ReadWrite so the workbook is still readable
        // while the user has the same file open in Excel.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var workbook = new XLWorkbook(stream);
        var datasets = new List<DlsDataset>();
        foreach (var ws in workbook.Worksheets)
        {
            if (ReadSheet(ws) is { } dataset)
                datasets.Add(dataset);
        }
        return datasets;
    }

    public static DlsDataset? ReadSheet(IXLWorksheet ws)
    {
        var lastColumn = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastColumn < 2 || lastRow < 2) return null;

        var headers = new DlsHeader[lastColumn];
        for (int c = 1; c <= lastColumn; c++)
            headers[c - 1] = DlsHeader.Parse(ws.Cell(1, c).GetString());

        var columnData = new double?[lastColumn][];
        for (int c = 0; c < lastColumn; c++)
            columnData[c] = new double?[lastRow - 1];

        for (int r = 2; r <= lastRow; r++)
            for (int c = 1; c <= lastColumn; c++)
                columnData[c - 1][r - 2] = TryGetNumber(ws.Cell(r, c));

        var numberRuns = new List<IReadOnlyList<double>>();
        var intensityRuns = new List<IReadOnlyList<double>>();
        var volumeRuns = new List<IReadOnlyList<double>>();
        var correlationRuns = new List<IReadOnlyList<double>>();
        IReadOnlyList<double>? sizeBins = null;
        IReadOnlyList<double>? correlationTimes = null;
        string? sampleLabel = null;

        foreach (var pair in ExtractPairs(headers))
        {
            var (xs, ys) = ExtractFiniteValues(columnData[pair.XIndex], columnData[pair.YIndex]);
            if (xs.Count == 0) continue;

            sampleLabel ??= headers[pair.XIndex].SampleLabel ?? headers[pair.YIndex].SampleLabel;

            if (headers[pair.XIndex].Kind == DlsColumnKind.SizeAxis)
            {
                // The shared X axis is captured from the first run. A later
                // run whose X column lost rows to empty cells / "---" would
                // emit a shorter ys than sizeBins and silently mis-align
                // with the bin labels downstream. Reject runs that disagree
                // on length or values so Runs stay rectangular.
                if (sizeBins is null)
                {
                    sizeBins = xs;
                }
                else if (!AxisMatches(sizeBins, xs))
                {
                    continue;
                }
                switch (headers[pair.YIndex].Kind)
                {
                    case DlsColumnKind.NumberPercent: numberRuns.Add(ys); break;
                    case DlsColumnKind.IntensityPercent: intensityRuns.Add(ys); break;
                    case DlsColumnKind.VolumePercent: volumeRuns.Add(ys); break;
                }
            }
            else if (headers[pair.XIndex].Kind == DlsColumnKind.TimeAxis &&
                     headers[pair.YIndex].Kind == DlsColumnKind.CorrelationG2Minus1)
            {
                // Normalize the time axis to microseconds. Zetasizer xlsx
                // defaults to μs ("Time (μs)") but some firmware exports
                // report seconds ("Time (s)") instead. CumulantAnalyzer
                // and SizeDistributionInverter assume μs throughout, so
                // a silent s-axis would shift Γ and the recovered size
                // by six orders of magnitude.
                var rawHeader = headers[pair.XIndex].Raw;
                var scaleToMicroseconds = ResolveTimeScaleToMicroseconds(rawHeader);
                IReadOnlyList<double> normalizedXs = xs;
                if (scaleToMicroseconds != 1.0)
                {
                    var scaled = new double[xs.Count];
                    for (int i = 0; i < xs.Count; i++) scaled[i] = xs[i] * scaleToMicroseconds;
                    normalizedXs = scaled;
                }
                if (correlationTimes is null)
                {
                    correlationTimes = normalizedXs;
                }
                else if (!AxisMatches(correlationTimes, normalizedXs))
                {
                    continue;
                }
                correlationRuns.Add(ys);
            }
        }

        if (sizeBins is null && correlationTimes is null) return null;

        return new DlsDataset
        {
            SheetName = ws.Name,
            SampleLabel = sampleLabel,
            NumberDistribution = BuildDistribution(sizeBins, numberRuns),
            IntensityDistribution = BuildDistribution(sizeBins, intensityRuns),
            VolumeDistribution = BuildDistribution(sizeBins, volumeRuns),
            Correlation = correlationTimes is not null && correlationRuns.Count > 0
                ? new CorrelationFunction { TimesMicroseconds = correlationTimes, Runs = correlationRuns, ActiveRunIndex = 0 }
                : null,
        };
    }

    private static ParticleSizeDistribution? BuildDistribution(
        IReadOnlyList<double>? sizeBins,
        IReadOnlyList<IReadOnlyList<double>> runs) =>
        sizeBins is not null && runs.Count > 0
            ? new ParticleSizeDistribution { SizeBinsNm = sizeBins, Runs = runs, ActiveRunIndex = 0 }
            : null;

    private static IEnumerable<(int XIndex, int YIndex)> ExtractPairs(DlsHeader[] headers)
    {
        int i = 0;
        while (i < headers.Length - 1)
        {
            var xKind = headers[i].Kind;
            var yKind = headers[i + 1].Kind;
            if (xKind == DlsColumnKind.SizeAxis && IsDistributionY(yKind))
            {
                yield return (i, i + 1);
                i += 2;
                continue;
            }
            if (xKind == DlsColumnKind.TimeAxis && yKind == DlsColumnKind.CorrelationG2Minus1)
            {
                yield return (i, i + 1);
                i += 2;
                continue;
            }
            i++;
        }
    }

    private static bool IsDistributionY(DlsColumnKind kind) =>
        kind is DlsColumnKind.NumberPercent or DlsColumnKind.IntensityPercent or DlsColumnKind.VolumePercent;

    private static (IReadOnlyList<double> X, IReadOnlyList<double> Y) ExtractFiniteValues(double?[] xCol, double?[] yCol)
    {
        var n = Math.Min(xCol.Length, yCol.Length);
        var xs = new List<double>(n);
        var ys = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            if (xCol[i] is double x && yCol[i] is double y && double.IsFinite(x) && double.IsFinite(y))
            {
                xs.Add(x);
                ys.Add(y);
            }
        }
        return (xs, ys);
    }

    private static double? TryGetNumber(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        if (cell.TryGetValue<double>(out var num) && double.IsFinite(num)) return num;
        return null;
    }

    private static double ResolveTimeScaleToMicroseconds(string headerRaw)
    {
        if (string.IsNullOrWhiteSpace(headerRaw)) return 1.0;
        // Check seconds variants first — "(s)" alone is unambiguous but
        // would also match "(ms)" / "(μs)" / "(us)" if substring tested
        // naively, so anchor on (s) bounded by parentheses or word break.
        if (HasUnitToken(headerRaw, "s") || HasUnitToken(headerRaw, "sec") || HasUnitToken(headerRaw, "seconds"))
            return 1e6;
        if (HasUnitToken(headerRaw, "ms") || HasUnitToken(headerRaw, "msec"))
            return 1e3;
        // Default: microseconds (Zetasizer's standard export).
        return 1.0;
    }

    private static bool HasUnitToken(string headerRaw, string token)
    {
        // Match the unit only when wrapped in parentheses, e.g. "Time (s)".
        var needle = "(" + token + ")";
        return headerRaw.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool AxisMatches(IReadOnlyList<double> reference, IReadOnlyList<double> candidate)
    {
        if (reference.Count != candidate.Count) return false;
        for (int i = 0; i < reference.Count; i++)
        {
            var r = reference[i];
            var c = candidate[i];
            if (r == 0.0 && c == 0.0) continue;
            var tol = Math.Max(1e-9 * Math.Max(Math.Abs(r), Math.Abs(c)), 1e-12);
            if (Math.Abs(r - c) > tol) return false;
        }
        return true;
    }
}
