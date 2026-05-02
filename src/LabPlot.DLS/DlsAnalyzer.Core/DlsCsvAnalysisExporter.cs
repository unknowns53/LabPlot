using System.Globalization;
using System.Text;
using LabPlot.Core;

namespace DlsAnalyzer.Core;

/// <summary>
/// CSV exporter for DLS analyses. Writes a header banner with the
/// generator string and timestamp, a per-sheet metadata + cumulant
/// summary table, and one X-Y data section per sheet.
/// </summary>
public sealed class DlsCsvAnalysisExporter : IAnalysisExporter
{
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    public void Export(AnalysisExport data, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        using var writer = new StreamWriter(filePath, false, Utf8WithBom);
        WriteText(writer, data);
    }

    /// <summary>For tests / previewing without touching disk.</summary>
    public string ToText(AnalysisExport data)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteText(writer, data);
        return builder.ToString();
    }

    private static void WriteText(TextWriter writer, AnalysisExport data)
    {
        var entries = data.Entries.Cast<DlsAnalysisExportEntry>().ToArray();
        WriteHeader(writer, data);
        WriteSummarySection(writer, entries);
        foreach (var entry in entries)
        {
            WriteDataSection(writer, entry);
        }
    }

    private static void WriteHeader(TextWriter writer, AnalysisExport data)
    {
        writer.WriteLine($"# {data.GeneratorName} analysis export");
        writer.WriteLine($"# Generated: {data.CreatedAt:O}");
        writer.WriteLine();
    }

    // One row per sheet capturing the metadata and cumulant outcome.
    // Empty cells indicate fields the user has not entered (and, for
    // cumulant columns, fits that did not run).
    private static void WriteSummarySection(
        TextWriter writer,
        IReadOnlyList<DlsAnalysisExportEntry> entries)
    {
        writer.WriteLine("# Summary");
        writer.WriteLine(string.Join(",",
            "Sheet", "Mode",
            "Temperature (°C)", "Solvent", "Concentration (mg/mL)",
            "Refractive Index", "Viscosity (mPa·s)",
            "Wavelength (nm)", "Scattering Angle (°)",
            "Z-average diameter (nm)", "PdI",
            "Γ (μs⁻¹)", "R²",
            "Fit range min (μs)", "Fit range max (μs)",
            "Fit point count"));
        foreach (var entry in entries)
        {
            writer.WriteLine(string.Join(",",
                Quote(entry.DisplayName),
                Quote(entry.DistributionMode),
                FormatNullable(entry.TemperatureCelsius),
                Quote(entry.Solvent ?? string.Empty),
                FormatNullable(entry.ConcentrationMgPerMl),
                FormatNullable(entry.RefractiveIndex),
                FormatNullable(entry.ViscosityMpas),
                FormatNullable(entry.WavelengthNm),
                FormatNullable(entry.ScatteringAngleDegrees),
                FormatNullable(entry.HydrodynamicDiameterNm),
                FormatNullable(entry.Cumulant?.PolydispersityIndex),
                FormatNullable(entry.Cumulant?.FirstCumulantPerMicrosecond),
                FormatNullable(entry.Cumulant?.RSquared),
                FormatNullable(entry.Cumulant?.AppliedRangeMinMicroseconds),
                FormatNullable(entry.Cumulant?.AppliedRangeMaxMicroseconds),
                entry.Cumulant?.PointCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }
        writer.WriteLine();
    }

    private static void WriteDataSection(TextWriter writer, DlsAnalysisExportEntry entry)
    {
        writer.WriteLine($"# Data ({entry.DisplayName} / {entry.DistributionMode})");
        writer.WriteLine(string.Join(",", Quote(entry.XLabel), Quote(entry.YLabel)));
        var n = Math.Min(entry.Xs.Count, entry.Ys.Count);
        for (int i = 0; i < n; i++)
        {
            writer.WriteLine(string.Join(",",
                FormatDouble(entry.Xs[i]),
                FormatDouble(entry.Ys[i])));
        }
        writer.WriteLine();
    }

    private static string FormatNullable(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value)) return string.Empty;
        return value.Value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string FormatDouble(double value)
        => double.IsFinite(value) ? value.ToString("G", CultureInfo.InvariantCulture) : string.Empty;

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
