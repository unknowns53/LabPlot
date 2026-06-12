using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace DataViewer.Core;

/// <summary>
/// Reads CSV / TSV / semicolon / whitespace-delimited text into a single
/// <see cref="ViewerTable"/>. Delimiter is guessed from the first
/// non-blank line (same precedence as the GPC reader); delimited variants
/// run through CsvHelper so quoted fields with embedded delimiters parse
/// correctly, the whitespace fallback uses a simple tokenizer.
/// </summary>
public sealed class DelimitedTextTableReader : IViewerDataReader
{
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

    public ViewerTableSet Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Data file was not found.", filePath);
        }

        using var reader = new StreamReader(filePath, LenientUtf8, true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        var table = ParseLines(lines, filePath);
        return new ViewerTableSet { Tables = new[] { table } };
    }

    internal static ViewerTable ParseLines(IReadOnlyList<string> lines, string? sourceFilePath)
    {
        var firstLine = lines.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? throw new InvalidDataException("The file is empty.");

        var delimiter = GuessDelimiter(firstLine);
        var rows = delimiter is null
            ? SplitWhitespaceRows(lines)
            : SplitDelimitedRows(lines, delimiter);
        return TableBuilder.Build(rows, sourceFilePath, sheetName: null);
    }

    internal static string? GuessDelimiter(string headerLine)
    {
        var candidates = new[] { ",", "\t", ";" };
        return candidates
            .Select(delimiter => new { Delimiter = delimiter, Count = headerLine.Split(delimiter).Length })
            .Where(static candidate => candidate.Count >= 2)
            .OrderByDescending(static candidate => candidate.Count)
            .Select(static candidate => candidate.Delimiter)
            .FirstOrDefault();
    }

    private static List<IReadOnlyList<string>> SplitDelimitedRows(
        IReadOnlyList<string> lines,
        string delimiter)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            Delimiter = delimiter,
            HasHeaderRecord = false,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        var rows = new List<IReadOnlyList<string>>();
        using var parser = new CsvParser(
            new StringReader(string.Join(Environment.NewLine, lines)), config);
        while (parser.Read())
        {
            rows.Add(parser.Record ?? Array.Empty<string>());
        }

        return rows;
    }

    private static List<IReadOnlyList<string>> SplitWhitespaceRows(IReadOnlyList<string> lines)
    {
        var rows = new List<IReadOnlyList<string>>(lines.Count);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            rows.Add(SplitOnWhitespace(line.AsSpan().Trim()));
        }

        return rows;
    }

    internal static string[] SplitOnWhitespace(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>(capacity: 8);
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i]))
            {
                i++;
            }

            var start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i]))
            {
                i++;
            }

            if (i > start)
            {
                tokens.Add(span[start..i].ToString());
            }
        }

        return tokens.ToArray();
    }
}
