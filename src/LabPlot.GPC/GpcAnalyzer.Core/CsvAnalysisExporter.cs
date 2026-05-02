using System.Globalization;
using System.Text;
using LabPlot.Core;

namespace GpcAnalyzer.Core;

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
        var entries = data.Entries.Cast<GpcAnalysisExportEntry>().ToArray();
        WriteHeader(writer, data);
        WriteStatisticsSection(writer, entries);
        WritePeakListSection(writer, entries);
        foreach (var entry in entries)
        {
            WriteChromatogramSection(writer, entry);
        }

        foreach (var entry in entries)
        {
            if (entry.MolecularWeightDataset is null)
            {
                continue;
            }

            WriteMolecularWeightSection(writer, entry);
        }
    }

    private static void WriteHeader(TextWriter writer, AnalysisExport data)
    {
        writer.WriteLine($"# {data.GeneratorName} analysis export");
        writer.WriteLine($"# Generated: {data.CreatedAt:O}");
        writer.WriteLine();
    }

    private static void WriteStatisticsSection(
        TextWriter writer,
        IReadOnlyList<GpcAnalysisExportEntry> entries)
    {
        writer.WriteLine("# Statistics");
        writer.WriteLine("File,Detector,Mn,Mw,Dispersity (Ð),Source,SelectedPeak");
        foreach (var entry in entries)
        {
            var stats = entry.Statistics;
            writer.WriteLine(string.Join(
                ",",
                Quote(entry.DisplayName),
                Quote(entry.Detector ?? string.Empty),
                FormatNullable(stats?.Mn),
                FormatNullable(stats?.Mw),
                FormatNullable(stats?.Pdi),
                stats is null ? string.Empty : stats.Source.ToString(),
                Quote(stats?.SelectedPeakId ?? (stats?.IsAutoSelected == true ? "auto" : string.Empty))));
        }

        writer.WriteLine();
    }

    private static void WritePeakListSection(
        TextWriter writer,
        IReadOnlyList<GpcAnalysisExportEntry> entries)
    {
        writer.WriteLine("# Peak List");
        writer.WriteLine("File,Detector,Peak#,Mn,Mw,Dispersity (Ð),Percent");
        foreach (var entry in entries)
        {
            var stats = entry.Statistics;
            if (stats is null || stats.Peaks.Count == 0)
            {
                continue;
            }

            foreach (var peak in stats.Peaks)
            {
                writer.WriteLine(string.Join(
                    ",",
                    Quote(entry.DisplayName),
                    Quote(entry.Detector ?? string.Empty),
                    Quote(peak.PeakId),
                    FormatNullable(peak.Mn),
                    FormatNullable(peak.Mw),
                    FormatNullable(peak.Pdi),
                    FormatNullable(peak.Percent)));
            }
        }

        writer.WriteLine();
    }

    private static void WriteChromatogramSection(TextWriter writer, GpcAnalysisExportEntry entry)
    {
        var label = string.IsNullOrWhiteSpace(entry.Detector)
            ? entry.DisplayName
            : $"{entry.DisplayName}, Detector {entry.Detector}";
        writer.WriteLine($"# Chromatogram ({label})");
        writer.WriteLine(string.Join(",", Quote(entry.XLabel), Quote(entry.YLabel)));
        foreach (var point in entry.ChromatogramPoints)
        {
            writer.WriteLine(string.Join(
                ",",
                FormatDouble(point.X),
                FormatDouble(point.Y)));
        }

        writer.WriteLine();
    }

    private static void WriteMolecularWeightSection(TextWriter writer, GpcAnalysisExportEntry entry)
    {
        var dataset = entry.MolecularWeightDataset!;
        var label = string.IsNullOrWhiteSpace(entry.Detector)
            ? entry.DisplayName
            : $"{entry.DisplayName}, Detector {entry.Detector}";
        var signalHeader = dataset.YMode == MolecularWeightYMode.DifferentialWeightFraction
            ? "dw/dlogM"
            : "Signal";
        writer.WriteLine($"# Molecular Weight ({label})");
        writer.WriteLine($"RetentionTime,MolecularWeight,log10(M),{Quote(signalHeader)}");
        foreach (var point in dataset.Points)
        {
            writer.WriteLine(string.Join(
                ",",
                FormatDouble(point.RetentionTime),
                FormatDouble(point.MolecularWeight),
                FormatDouble(point.LogMolecularWeight),
                FormatDouble(point.Signal)));
        }

        writer.WriteLine();
    }

    private static string FormatNullable(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return string.Empty;
        }

        return value.Value.ToString("G", CultureInfo.InvariantCulture);
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
