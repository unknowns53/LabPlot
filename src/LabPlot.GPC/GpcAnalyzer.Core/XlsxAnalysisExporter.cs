using ClosedXML.Excel;

namespace GpcAnalyzer.Core;

public sealed class XlsxAnalysisExporter : IAnalysisExporter
{
    public void Export(AnalysisExport data, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        using var workbook = new XLWorkbook();
        AddStatisticsSheet(workbook, data);
        AddPeakListSheet(workbook, data);
        AddChromatogramSheet(workbook, data);
        AddMolecularWeightSheet(workbook, data);

        if (workbook.Worksheets.Count == 0)
        {
            workbook.AddWorksheet("Empty");
        }

        workbook.SaveAs(filePath);
    }

    private static void AddStatisticsSheet(XLWorkbook workbook, AnalysisExport data)
    {
        var sheet = workbook.AddWorksheet("Statistics");
        sheet.Cell(1, 1).Value = "Generated";
        sheet.Cell(1, 2).Value = data.CreatedAt.ToString("u");
        sheet.Cell(2, 1).Value = "Generator";
        sheet.Cell(2, 2).Value = data.GeneratorName;

        var headerRow = 4;
        sheet.Cell(headerRow, 1).Value = "File";
        sheet.Cell(headerRow, 2).Value = "Detector";
        sheet.Cell(headerRow, 3).Value = "Mn";
        sheet.Cell(headerRow, 4).Value = "Mw";
        sheet.Cell(headerRow, 5).Value = "Dispersity (Ð)";
        sheet.Cell(headerRow, 6).Value = "Source";
        sheet.Cell(headerRow, 7).Value = "Selected Peak";

        var row = headerRow + 1;
        foreach (var entry in data.Entries)
        {
            sheet.Cell(row, 1).Value = entry.DisplayName;
            sheet.Cell(row, 2).Value = entry.Detector ?? string.Empty;
            SetNumeric(sheet.Cell(row, 3), entry.Statistics?.Mn);
            SetNumeric(sheet.Cell(row, 4), entry.Statistics?.Mw);
            SetNumeric(sheet.Cell(row, 5), entry.Statistics?.Pdi);
            sheet.Cell(row, 6).Value = entry.Statistics?.Source.ToString() ?? string.Empty;
            sheet.Cell(row, 7).Value = ResolveSelectedPeakLabel(entry.Statistics);
            row++;
        }

        sheet.Range(headerRow, 1, headerRow, 7).Style.Font.Bold = true;
        sheet.Columns().AdjustToContents();
    }

    private static void AddPeakListSheet(XLWorkbook workbook, AnalysisExport data)
    {
        var hasPeaks = data.Entries.Any(entry => entry.Statistics?.Peaks.Count > 0);
        if (!hasPeaks)
        {
            return;
        }

        var sheet = workbook.AddWorksheet("Peak List");
        sheet.Cell(1, 1).Value = "File";
        sheet.Cell(1, 2).Value = "Detector";
        sheet.Cell(1, 3).Value = "Peak#";
        sheet.Cell(1, 4).Value = "Mn";
        sheet.Cell(1, 5).Value = "Mw";
        sheet.Cell(1, 6).Value = "Dispersity (Ð)";
        sheet.Cell(1, 7).Value = "Percent";

        var row = 2;
        foreach (var entry in data.Entries)
        {
            var stats = entry.Statistics;
            if (stats is null || stats.Peaks.Count == 0)
            {
                continue;
            }

            foreach (var peak in stats.Peaks)
            {
                sheet.Cell(row, 1).Value = entry.DisplayName;
                sheet.Cell(row, 2).Value = entry.Detector ?? string.Empty;
                sheet.Cell(row, 3).Value = peak.PeakId;
                SetNumeric(sheet.Cell(row, 4), peak.Mn);
                SetNumeric(sheet.Cell(row, 5), peak.Mw);
                SetNumeric(sheet.Cell(row, 6), peak.Pdi);
                SetNumeric(sheet.Cell(row, 7), peak.Percent);
                row++;
            }
        }

        sheet.Range(1, 1, 1, 7).Style.Font.Bold = true;
        sheet.Columns().AdjustToContents();
    }

    private static void AddChromatogramSheet(XLWorkbook workbook, AnalysisExport data)
    {
        var hasChromatogram = data.Entries.Any(entry => entry.ChromatogramPoints.Count > 0);
        if (!hasChromatogram)
        {
            return;
        }

        var sheet = workbook.AddWorksheet("Chromatogram");
        var col = 1;
        foreach (var entry in data.Entries)
        {
            if (entry.ChromatogramPoints.Count == 0)
            {
                continue;
            }

            sheet.Cell(1, col).Value = BuildSeriesLabel(entry);
            sheet.Range(1, col, 1, col + 1).Merge().Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(2, col).Value = entry.XLabel;
            sheet.Cell(2, col + 1).Value = entry.YLabel;
            for (var i = 0; i < entry.ChromatogramPoints.Count; i++)
            {
                var point = entry.ChromatogramPoints[i];
                SetNumeric(sheet.Cell(3 + i, col), point.X);
                SetNumeric(sheet.Cell(3 + i, col + 1), point.Y);
            }

            col += 2;
        }

        sheet.Range(1, 1, 2, col - 1).Style.Font.Bold = true;
        sheet.Columns(1, col - 1).Width = 16;
    }

    private static void AddMolecularWeightSheet(XLWorkbook workbook, AnalysisExport data)
    {
        var entriesWithMw = data.Entries
            .Where(entry => entry.MolecularWeightDataset is not null && entry.MolecularWeightDataset.Points.Count > 0)
            .ToArray();
        if (entriesWithMw.Length == 0)
        {
            return;
        }

        var sheet = workbook.AddWorksheet("Molecular Weight");
        var col = 1;
        foreach (var entry in entriesWithMw)
        {
            var dataset = entry.MolecularWeightDataset!;
            var signalHeader = dataset.YMode == MolecularWeightYMode.DifferentialWeightFraction
                ? "dw/dlogM"
                : "Signal";

            sheet.Cell(1, col).Value = BuildSeriesLabel(entry);
            sheet.Range(1, col, 1, col + 3).Merge().Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(2, col).Value = "RetentionTime";
            sheet.Cell(2, col + 1).Value = "MolecularWeight";
            sheet.Cell(2, col + 2).Value = "log10(M)";
            sheet.Cell(2, col + 3).Value = signalHeader;

            for (var i = 0; i < dataset.Points.Count; i++)
            {
                var point = dataset.Points[i];
                SetNumeric(sheet.Cell(3 + i, col), point.RetentionTime);
                SetNumeric(sheet.Cell(3 + i, col + 1), point.MolecularWeight);
                SetNumeric(sheet.Cell(3 + i, col + 2), point.LogMolecularWeight);
                SetNumeric(sheet.Cell(3 + i, col + 3), point.Signal);
            }

            col += 4;
        }

        sheet.Range(1, 1, 2, col - 1).Style.Font.Bold = true;
        sheet.Columns(1, col - 1).Width = 16;
    }

    private static string BuildSeriesLabel(AnalysisExportEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Detector))
        {
            return entry.DisplayName;
        }

        return $"{entry.DisplayName} (Detector {entry.Detector})";
    }

    private static string ResolveSelectedPeakLabel(MolecularWeightStatistics? statistics)
    {
        if (statistics is null)
        {
            return string.Empty;
        }

        if (statistics.SelectedPeakId is { } selected)
        {
            return selected;
        }

        if (statistics.IsAutoSelected && statistics.Peaks.Count > 0)
        {
            var auto = MolecularWeightStatistics.SelectAutoRepresentativePeak(statistics.Peaks);
            return auto is null ? "auto" : $"auto ({auto.PeakId})";
        }

        return string.Empty;
    }

    private static void SetNumeric(IXLCell cell, double? value)
    {
        if (value.HasValue && double.IsFinite(value.Value))
        {
            cell.Value = value.Value;
        }
    }

    private static void SetNumeric(IXLCell cell, double value)
    {
        if (double.IsFinite(value))
        {
            cell.Value = value;
        }
    }
}
