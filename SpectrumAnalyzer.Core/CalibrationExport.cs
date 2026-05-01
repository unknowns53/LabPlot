using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// One row in a calibration export — a single sample's contribution to
/// the curve. Carries both the user-facing concentration (in the
/// configured unit) and the molar value actually used by the fit so the
/// spreadsheet is self-explanatory.
/// </summary>
public sealed class CalibrationExportRow
{
    public required string DatasetName { get; init; }

    public required double? ConcentrationInUnit { get; init; }

    public required double ConcentrationMolar { get; init; }

    public required double Signal { get; init; }

    public required double Predicted { get; init; }

    public required double Residual { get; init; }

    public required bool IsExcluded { get; init; }
}

/// <summary>
/// CSV / XLSX exporter for a Beer-Lambert calibration curve. Layout: a
/// summary header (mode / wavelength or region / l / fit options / ε /
/// R² / N) followed by one row per sample. Mirrors
/// <see cref="IntegrationExport"/>'s style so the two outputs feel
/// consistent in a spreadsheet.
/// </summary>
public sealed class CalibrationExport
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    private static readonly string[] HeaderRow =
    {
        "Dataset",
        "Concentration",
        "Unit",
        "Concentration (M)",
        "Signal",
        "Predicted",
        "Residual",
        "Excluded",
    };

    public required CalibrationCurveConfig Config { get; init; }

    public required CalibrationResult Result { get; init; }

    public IReadOnlyList<CalibrationExportRow> Rows { get; init; } = Array.Empty<CalibrationExportRow>();

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
        var sheet = workbook.AddWorksheet("Calibration");

        var unitSymbol = CalibrationUnitConverter.GetSymbol(Config.ConcentrationUnit);

        sheet.Cell(1, 1).Value = "Generated";
        sheet.Cell(1, 2).Value = CreatedAt.ToString("u");
        sheet.Cell(2, 1).Value = "Generator";
        sheet.Cell(2, 2).Value = GeneratorName;
        sheet.Cell(3, 1).Value = "Quantification";
        sheet.Cell(3, 2).Value = FormatMode(Config);
        sheet.Cell(4, 1).Value = "Path length (cm)";
        SetNumeric(sheet.Cell(4, 2), Config.PathLengthCm);
        sheet.Cell(5, 1).Value = "Fit mode";
        sheet.Cell(5, 2).Value = Result.FitMode == CalibrationFitMode.ForceOrigin
            ? "Forced origin (y = m x)"
            : "With intercept (y = m x + b)";
        sheet.Cell(6, 1).Value = "Unit";
        sheet.Cell(6, 2).Value = unitSymbol;
        if (CalibrationUnitConverter.RequiresMolarMass(Config.ConcentrationUnit))
        {
            sheet.Cell(7, 1).Value = "Molar mass (g/mol)";
            SetNumeric(sheet.Cell(7, 2), Config.MolarMass ?? double.NaN);
        }

        sheet.Cell(8, 1).Value = "Slope";
        SetNumeric(sheet.Cell(8, 2), Result.Slope);
        sheet.Cell(9, 1).Value = "Intercept";
        SetNumeric(sheet.Cell(9, 2), Result.Intercept);
        sheet.Cell(10, 1).Value = "R²";
        SetNumeric(sheet.Cell(10, 2), Result.RSquared);
        sheet.Cell(11, 1).Value = "N";
        sheet.Cell(11, 2).Value = Result.N;
        sheet.Cell(12, 1).Value = "ε (M⁻¹·cm⁻¹)";
        SetNumeric(sheet.Cell(12, 2), Result.EpsilonPerCmPerMolar);

        sheet.Range(1, 1, 12, 1).Style.Font.Bold = true;

        for (var c = 0; c < HeaderRow.Length; c++)
        {
            sheet.Cell(14, c + 1).Value = HeaderRow[c];
        }

        sheet.Range(14, 1, 14, HeaderRow.Length).Style.Font.Bold = true;

        var r = 15;
        foreach (var row in Rows)
        {
            sheet.Cell(r, 1).Value = row.DatasetName;
            if (row.ConcentrationInUnit is { } conc && double.IsFinite(conc))
            {
                sheet.Cell(r, 2).Value = conc;
            }

            sheet.Cell(r, 3).Value = unitSymbol;
            SetNumeric(sheet.Cell(r, 4), row.ConcentrationMolar);
            SetNumeric(sheet.Cell(r, 5), row.Signal);
            SetNumeric(sheet.Cell(r, 6), row.Predicted);
            SetNumeric(sheet.Cell(r, 7), row.Residual);
            sheet.Cell(r, 8).Value = row.IsExcluded ? "Yes" : string.Empty;
            r++;
        }

        sheet.Columns(1, HeaderRow.Length).AdjustToContents();
        workbook.SaveAs(filePath);
    }

    private void WriteCsvCore(TextWriter writer)
    {
        var unitSymbol = CalibrationUnitConverter.GetSymbol(Config.ConcentrationUnit);

        writer.WriteLine($"# {GeneratorName} calibration export");
        writer.WriteLine($"# Generated: {CreatedAt:O}");
        writer.WriteLine($"# Quantification: {FormatMode(Config)}");
        writer.WriteLine(FormatInvariant(
            "# Path length (cm): {0}",
            Config.PathLengthCm));
        writer.WriteLine($"# Fit mode: {(Result.FitMode == CalibrationFitMode.ForceOrigin ? "Forced origin (y = m x)" : "With intercept (y = m x + b)")}");
        writer.WriteLine($"# Unit: {unitSymbol}");
        if (CalibrationUnitConverter.RequiresMolarMass(Config.ConcentrationUnit))
        {
            writer.WriteLine(FormatInvariant(
                "# Molar mass (g/mol): {0}",
                Config.MolarMass ?? double.NaN));
        }

        writer.WriteLine(FormatInvariant("# Slope: {0}", Result.Slope));
        writer.WriteLine(FormatInvariant("# Intercept: {0}", Result.Intercept));
        writer.WriteLine(FormatInvariant("# R²: {0}", Result.RSquared));
        writer.WriteLine($"# N: {Result.N.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine(FormatInvariant("# ε (M⁻¹·cm⁻¹): {0}", Result.EpsilonPerCmPerMolar));
        writer.WriteLine();
        writer.WriteLine(string.Join(",", HeaderRow));

        foreach (var row in Rows)
        {
            writer.WriteLine(string.Join(
                ",",
                Quote(row.DatasetName),
                FormatOptionalDouble(row.ConcentrationInUnit),
                Quote(unitSymbol),
                FormatDouble(row.ConcentrationMolar),
                FormatDouble(row.Signal),
                FormatDouble(row.Predicted),
                FormatDouble(row.Residual),
                row.IsExcluded ? "Yes" : string.Empty));
        }
    }

    private static string FormatMode(CalibrationCurveConfig config) => config.Mode switch
    {
        CalibrationQuantificationMode.SingleWavelength
            => $"Absorbance @ {config.WavelengthNm.ToString("0.###", CultureInfo.InvariantCulture)} nm",
        CalibrationQuantificationMode.IntegrationArea
            => string.IsNullOrWhiteSpace(config.IntegrationRegionLabel)
                ? "Integration area"
                : $"Integration area: {config.IntegrationRegionLabel}",
        _ => config.Mode.ToString(),
    };

    private static string FormatDouble(double value) =>
        double.IsFinite(value)
            ? value.ToString("G", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string FormatOptionalDouble(double? value) =>
        value is { } v ? FormatDouble(v) : string.Empty;

    private static string FormatInvariant(string format, double value) =>
        string.Format(CultureInfo.InvariantCulture, format, FormatDouble(value));

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
