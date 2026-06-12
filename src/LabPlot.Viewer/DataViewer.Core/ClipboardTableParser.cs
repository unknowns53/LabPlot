namespace DataViewer.Core;

/// <summary>
/// Parses tabular text pasted from the clipboard (Excel copies arrive as
/// tab-separated text) into a <see cref="ViewerTable"/>. Tab is preferred
/// outright; otherwise the delimiter is guessed the same way as for files.
/// </summary>
public static class ClipboardTableParser
{
    /// <summary>
    /// Upper bound on cells (rows × columns) so an accidental paste of a
    /// whole worksheet dump fails fast instead of freezing the UI.
    /// </summary>
    public const int MaxCellCount = 1_000_000;

    public static ViewerTable Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("Clipboard does not contain text.");
        }

        var lines = text.Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .ToList();

        var firstLine = lines.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? throw new InvalidDataException("Clipboard does not contain a table.");

        List<IReadOnlyList<string>> rows;
        if (text.Contains('\t', StringComparison.Ordinal))
        {
            rows = lines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => (IReadOnlyList<string>)line.Split('\t', StringSplitOptions.TrimEntries))
                .ToList();
        }
        else
        {
            var delimiter = DelimitedTextTableReader.GuessDelimiter(firstLine);
            rows = lines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(line => (IReadOnlyList<string>)(delimiter is null
                    ? DelimitedTextTableReader.SplitOnWhitespace(line.AsSpan().Trim())
                    : line.Split(delimiter, StringSplitOptions.TrimEntries)))
                .ToList();
        }

        var columnCount = rows.Count == 0 ? 0 : rows.Max(static row => row.Count);
        if ((long)rows.Count * columnCount > MaxCellCount)
        {
            throw new InvalidDataException(
                $"Pasted table is too large ({rows.Count} rows × {columnCount} columns; the limit is {MaxCellCount:N0} cells).");
        }

        return TableBuilder.Build(rows, sourceFilePath: null, sheetName: null);
    }
}
