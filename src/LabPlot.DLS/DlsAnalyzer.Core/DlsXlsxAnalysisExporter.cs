using ClosedXML.Excel;
using LabPlot.Core;

namespace DlsAnalyzer.Core;

/// <summary>
/// XLSX exporter for DLS analyses. Writes a Summary sheet with the
/// per-sheet metadata + cumulant outcome and one Data sheet per
/// exported entry containing the active mode's X-Y points.
/// </summary>
public sealed class DlsXlsxAnalysisExporter : IAnalysisExporter
{
    public void Export(AnalysisExport data, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        var entries = data.Entries.Cast<DlsAnalysisExportEntry>().ToArray();
        using var workbook = new XLWorkbook();
        AddSummarySheet(workbook, data, entries);
        AddDataSheets(workbook, entries);

        if (workbook.Worksheets.Count == 0)
        {
            workbook.AddWorksheet("Empty");
        }

        workbook.SaveAs(filePath);
    }

    private static void AddSummarySheet(
        XLWorkbook workbook,
        AnalysisExport data,
        IReadOnlyList<DlsAnalysisExportEntry> entries)
    {
        var sheet = workbook.AddWorksheet("Summary");
        sheet.Cell(1, 1).Value = "Generated";
        sheet.Cell(1, 2).Value = data.CreatedAt.ToString("u");
        sheet.Cell(2, 1).Value = "Generator";
        sheet.Cell(2, 2).Value = data.GeneratorName;

        var headerRow = 4;
        var headers = new[]
        {
            "Sheet", "Mode",
            "Temperature (°C)", "Solvent", "Concentration (mg/mL)",
            "Refractive Index", "Viscosity (mPa·s)",
            "Wavelength (nm)", "Scattering Angle (°)",
            "Z-average diameter (nm)", "PdI",
            "Γ (μs⁻¹)", "R²",
            "Fit range min (μs)", "Fit range max (μs)",
            "Fit point count",
        };
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.Cell(headerRow, i + 1).Value = headers[i];
        }

        var row = headerRow + 1;
        foreach (var entry in entries)
        {
            sheet.Cell(row, 1).Value = entry.DisplayName;
            sheet.Cell(row, 2).Value = entry.DistributionMode;
            SetNullable(sheet.Cell(row, 3), entry.TemperatureCelsius);
            sheet.Cell(row, 4).Value = entry.Solvent ?? string.Empty;
            SetNullable(sheet.Cell(row, 5), entry.ConcentrationMgPerMl);
            SetNullable(sheet.Cell(row, 6), entry.RefractiveIndex);
            SetNullable(sheet.Cell(row, 7), entry.ViscosityMpas);
            SetNullable(sheet.Cell(row, 8), entry.WavelengthNm);
            SetNullable(sheet.Cell(row, 9), entry.ScatteringAngleDegrees);
            SetNullable(sheet.Cell(row, 10), entry.HydrodynamicDiameterNm);
            SetNullable(sheet.Cell(row, 11), entry.Cumulant?.PolydispersityIndex);
            SetNullable(sheet.Cell(row, 12), entry.Cumulant?.FirstCumulantPerMicrosecond);
            SetNullable(sheet.Cell(row, 13), entry.Cumulant?.RSquared);
            SetNullable(sheet.Cell(row, 14), entry.Cumulant?.AppliedRangeMinMicroseconds);
            SetNullable(sheet.Cell(row, 15), entry.Cumulant?.AppliedRangeMaxMicroseconds);
            if (entry.Cumulant is not null)
            {
                sheet.Cell(row, 16).Value = entry.Cumulant.PointCount;
            }
            row++;
        }
    }

    // One sheet per export entry holding the X-Y data for the displayed
    // mode. Sheet names are derived from DisplayName but trimmed /
    // sanitised to fit Excel's 31-character + restricted-character
    // rules. Duplicate names get a numeric suffix.
    private static void AddDataSheets(
        XLWorkbook workbook,
        IReadOnlyList<DlsAnalysisExportEntry> entries)
    {
        foreach (var entry in entries)
        {
            var name = MakeUniqueSheetName(workbook, entry.DisplayName);
            var sheet = workbook.AddWorksheet(name);
            sheet.Cell(1, 1).Value = "Sheet";
            sheet.Cell(1, 2).Value = entry.DisplayName;
            sheet.Cell(2, 1).Value = "Mode";
            sheet.Cell(2, 2).Value = entry.DistributionMode;

            sheet.Cell(4, 1).Value = entry.XLabel;
            sheet.Cell(4, 2).Value = entry.YLabel;
            var n = Math.Min(entry.Xs.Count, entry.Ys.Count);
            for (int i = 0; i < n; i++)
            {
                sheet.Cell(5 + i, 1).Value = entry.Xs[i];
                sheet.Cell(5 + i, 2).Value = entry.Ys[i];
            }
        }
    }

    private static string MakeUniqueSheetName(XLWorkbook workbook, string baseName)
    {
        // Excel forbids these characters and limits names to 31 chars.
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var sanitized = string.Concat(baseName.Where(c => !invalid.Contains(c)));
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "Sheet";
        if (sanitized.Length > 28) sanitized = sanitized[..28];

        var name = sanitized;
        var suffix = 2;
        while (workbook.Worksheets.Any(ws => string.Equals(ws.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            var bump = $" ({suffix})";
            var room = 31 - bump.Length;
            var trimmed = sanitized.Length > room ? sanitized[..room] : sanitized;
            name = trimmed + bump;
            suffix++;
        }
        return name;
    }

    private static void SetNullable(IXLCell cell, double? value)
    {
        if (value.HasValue && double.IsFinite(value.Value))
        {
            cell.Value = value.Value;
        }
    }
}
