using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// One row in an integration export — a single dataset × region pair.
/// </summary>
public sealed class IntegrationExportRow
{
    public required string DatasetName { get; init; }

    public required IntegrationRegion Region { get; init; }

    public required IntegrationResult Result { get; init; }

    /// <summary>
    /// The YUNITS recorded in the source file (e.g. <c>"ABSORBANCE"</c>,
    /// <c>"TRANSMITTANCE"</c>) so the user can disambiguate the integral
    /// when revisiting the spreadsheet later.
    /// </summary>
    public required string YUnits { get; init; }
}

/// <summary>
/// Self-contained CSV / XLSX exporter for the integration result table.
/// Lives next to the data instead of behind <see cref="IAnalysisExporter"/>
/// because the layout is wholly different from the per-dataset point-list
/// export — a flat table of dataset × region rows rather than columns of
/// (X, Y) samples.
/// </summary>
public sealed class IntegrationExport
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    private static readonly string[] HeaderRow =
    {
        "Dataset", "Label", "XMin", "XMax", "Baseline",
        "Area", "RawArea", "BaselineArea", "PointCount", "YUNITS",
    };

    public IReadOnlyList<IntegrationExportRow> Rows { get; init; } = Array.Empty<IntegrationExportRow>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string GeneratorName { get; init; } = "Spectrum Visualization";

    public void WriteCsv(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        using var writer = new StreamWriter(filePath, false, Utf8WithBom);
        WriteCsvCore(writer);
    }

    public string ToCsv()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsvCore(writer);
        return writer.ToString();
    }

    public void WriteXlsx(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Integration");

        sheet.Cell(1, 1).Value = "Generated";
        sheet.Cell(1, 2).Value = CreatedAt.ToString("u");
        sheet.Cell(2, 1).Value = "Generator";
        sheet.Cell(2, 2).Value = GeneratorName;

        for (var c = 0; c < HeaderRow.Length; c++)
        {
            sheet.Cell(4, c + 1).Value = HeaderRow[c];
        }

        sheet.Range(4, 1, 4, HeaderRow.Length).Style.Font.Bold = true;

        var r = 5;
        foreach (var row in Rows)
        {
            sheet.Cell(r, 1).Value = row.DatasetName;
            sheet.Cell(r, 2).Value = row.Region.Label;
            SetNumeric(sheet.Cell(r, 3), row.Region.XMin);
            SetNumeric(sheet.Cell(r, 4), row.Region.XMax);
            sheet.Cell(r, 5).Value = FormatBaseline(row.Region);
            SetNumeric(sheet.Cell(r, 6), row.Result.Area);
            SetNumeric(sheet.Cell(r, 7), row.Result.RawArea);
            SetNumeric(sheet.Cell(r, 8), row.Result.BaselineArea);
            sheet.Cell(r, 9).Value = row.Result.PointCount;
            sheet.Cell(r, 10).Value = row.YUnits;
            r++;
        }

        sheet.Columns(1, HeaderRow.Length).AdjustToContents();
        workbook.SaveAs(filePath);
    }

    private void WriteCsvCore(TextWriter writer)
    {
        writer.WriteLine($"# {GeneratorName} integration export");
        writer.WriteLine($"# Generated: {CreatedAt:O}");
        writer.WriteLine();
        writer.WriteLine(string.Join(",", HeaderRow));

        foreach (var row in Rows)
        {
            writer.WriteLine(string.Join(
                ",",
                Quote(row.DatasetName),
                Quote(row.Region.Label),
                FormatDouble(row.Region.XMin),
                FormatDouble(row.Region.XMax),
                Quote(FormatBaseline(row.Region)),
                FormatDouble(row.Result.Area),
                FormatDouble(row.Result.RawArea),
                FormatDouble(row.Result.BaselineArea),
                row.Result.PointCount.ToString(CultureInfo.InvariantCulture),
                Quote(row.YUnits)));
        }
    }

    private static string FormatBaseline(IntegrationRegion region) => region.BaselineMethod switch
    {
        BaselineMethod.RubberBand => $"RubberBand({region.RubberBandSegments})",
        BaselineMethod.RubberBandHull => $"RubberBandHull({region.RubberBandSegments})",
        BaselineMethod.Polynomial => $"Polynomial({region.PolynomialOrder})",
        _ => region.BaselineMethod.ToString(),
    };

    private static string FormatDouble(double value)
    {
        return double.IsFinite(value)
            ? value.ToString("G", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void SetNumeric(IXLCell cell, double value)
    {
        if (double.IsFinite(value))
        {
            cell.Value = value;
        }
    }
}
