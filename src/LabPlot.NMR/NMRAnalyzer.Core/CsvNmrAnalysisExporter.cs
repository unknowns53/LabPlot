using System.Globalization;
using System.Text;
using LabPlot.Core;

namespace NMRAnalyzer.Core;

/// <summary>
/// Writes NMR analysis exports as CSV. Ported from
/// <c>SpectrumAnalyzer.Core.CsvAnalysisExporter</c>: one section per dataset
/// with a ppm/intensity table. <see cref="IntegrationTableToText"/> /
/// <see cref="WriteIntegrationTable"/> emit the separate integration summary
/// (region, range, area, ratio).
/// </summary>
public sealed class CsvNmrAnalysisExporter : IAnalysisExporter
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

    public string ToText(AnalysisExport data)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteText(writer, data);
        return builder.ToString();
    }

    /// <summary>
    /// Render the integration summary table (one row per region) as CSV text.
    /// </summary>
    public static string IntegrationTableToText(IReadOnlyList<NmrIntegrationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteIntegrationSection(writer, results);
        return builder.ToString();
    }

    public static void WriteIntegrationTable(IReadOnlyList<NmrIntegrationResult> results, string filePath)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Output file path is required.", nameof(filePath));
        }

        using var writer = new StreamWriter(filePath, false, Utf8WithBom);
        WriteIntegrationSection(writer, results);
    }

    private static void WriteText(TextWriter writer, AnalysisExport data)
    {
        writer.WriteLine($"# {data.GeneratorName} analysis export");
        writer.WriteLine($"# Generated: {data.CreatedAt:O}");
        writer.WriteLine();

        foreach (var entry in data.Entries.Cast<NmrAnalysisExportEntry>())
        {
            writer.WriteLine($"# Spectrum ({entry.DisplayName})");
            writer.WriteLine(string.Join(",", Quote(entry.XLabel), Quote(entry.YLabel)));
            foreach (var point in entry.Points)
            {
                writer.WriteLine(string.Join(",", FormatDouble(point.Ppm), FormatDouble(point.Intensity)));
            }

            writer.WriteLine();
        }
    }

    private static void WriteIntegrationSection(TextWriter writer, IReadOnlyList<NmrIntegrationResult> results)
    {
        writer.WriteLine("Region,PpmMin,PpmMax,Area,Ratio,PointCount");
        foreach (var result in results)
        {
            writer.WriteLine(string.Join(
                ",",
                Quote(result.Region.Label),
                FormatDouble(result.Region.PpmMin),
                FormatDouble(result.Region.PpmMax),
                FormatDouble(result.Area),
                FormatDouble(result.Ratio),
                result.PointCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string FormatDouble(double value) =>
        double.IsFinite(value) ? value.ToString("G", CultureInfo.InvariantCulture) : string.Empty;

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
}
