using System.Globalization;
using System.Text;

namespace SpectrumAnalyzer.Core;

public sealed class CsvAnalysisExporter : IAnalysisExporter
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

    private static void WriteText(TextWriter writer, AnalysisExport data)
    {
        WriteHeader(writer, data);
        foreach (var entry in data.Entries)
        {
            WriteSpectrumSection(writer, entry);
        }
    }

    private static void WriteHeader(TextWriter writer, AnalysisExport data)
    {
        writer.WriteLine($"# {data.GeneratorName} analysis export");
        writer.WriteLine($"# Generated: {data.CreatedAt:O}");
        writer.WriteLine();
    }

    private static void WriteSpectrumSection(TextWriter writer, AnalysisExportEntry entry)
    {
        writer.WriteLine($"# Spectrum ({entry.DisplayName})");
        writer.WriteLine(string.Join(",", Quote(entry.XLabel), Quote(entry.YLabel)));
        foreach (var point in entry.Points)
        {
            writer.WriteLine(string.Join(
                ",",
                FormatDouble(point.X),
                FormatDouble(point.Y)));
        }

        writer.WriteLine();
    }

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
}
