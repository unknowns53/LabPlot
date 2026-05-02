using ClosedXML.Excel;
using LabPlot.Core;

namespace SpectrumAnalyzer.Core;

public sealed class XlsxAnalysisExporter : IAnalysisExporter
{
    public void Export(AnalysisExport data, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        var entries = data.Entries.Cast<SpectrumAnalysisExportEntry>().ToArray();
        using var workbook = new XLWorkbook();
        AddSpectrumSheet(workbook, data, entries);

        if (workbook.Worksheets.Count == 0)
        {
            workbook.AddWorksheet("Empty");
        }

        workbook.SaveAs(filePath);
    }

    private static void AddSpectrumSheet(
        XLWorkbook workbook,
        AnalysisExport data,
        IReadOnlyList<SpectrumAnalysisExportEntry> entries)
    {
        var hasData = entries.Any(entry => entry.Points.Count > 0);
        if (!hasData)
        {
            return;
        }

        var sheet = workbook.AddWorksheet("Spectrum");
        sheet.Cell(1, 1).Value = "Generated";
        sheet.Cell(1, 2).Value = data.CreatedAt.ToString("u");
        sheet.Cell(2, 1).Value = "Generator";
        sheet.Cell(2, 2).Value = data.GeneratorName;

        var headerRow = 4;
        var col = 1;
        foreach (var entry in entries)
        {
            if (entry.Points.Count == 0)
            {
                continue;
            }

            sheet.Cell(headerRow, col).Value = entry.DisplayName;
            sheet.Range(headerRow, col, headerRow, col + 1).Merge().Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(headerRow + 1, col).Value = entry.XLabel;
            sheet.Cell(headerRow + 1, col + 1).Value = entry.YLabel;
            for (var i = 0; i < entry.Points.Count; i++)
            {
                var point = entry.Points[i];
                SetNumeric(sheet.Cell(headerRow + 2 + i, col), point.X);
                SetNumeric(sheet.Cell(headerRow + 2 + i, col + 1), point.Y);
            }

            col += 2;
        }

        sheet.Range(headerRow, 1, headerRow + 1, col - 1).Style.Font.Bold = true;
        sheet.Columns(1, col - 1).Width = 16;
    }

    private static void SetNumeric(IXLCell cell, double value)
    {
        if (double.IsFinite(value))
        {
            cell.Value = value;
        }
    }
}
