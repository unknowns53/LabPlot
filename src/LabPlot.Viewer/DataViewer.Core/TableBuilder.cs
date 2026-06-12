namespace DataViewer.Core;

/// <summary>
/// Converts raw string cell rows (from any reader) into a rectangular
/// <see cref="ViewerTable"/>: detects the header row, pads ragged rows
/// with NaN, and classifies each column as numeric or not.
/// </summary>
internal static class TableBuilder
{
    /// <summary>
    /// Minimum fraction of non-empty cells that must parse as finite
    /// numbers for a column to count as numeric.
    /// </summary>
    private const double NumericCellRatioThreshold = 0.8;

    public static ViewerTable Build(
        IReadOnlyList<IReadOnlyList<string>> rawRows,
        string? sourceFilePath,
        string? sheetName)
    {
        var rows = rawRows
            .Where(static row => row.Any(static cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList();
        if (rows.Count == 0)
        {
            throw new InvalidDataException("The table contains no data rows.");
        }

        var columnCount = rows.Max(static row => row.Count);
        var hasHeader = DetectHeaderRow(rows);
        var headerCells = hasHeader ? rows[0] : null;
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows;
        if (dataRows.Count == 0)
        {
            throw new InvalidDataException("The table only contains a header row.");
        }

        var columns = new ViewerColumn[columnCount];
        for (var col = 0; col < columnCount; col++)
        {
            columns[col] = BuildColumn(dataRows, col, GetColumnName(headerCells, col));
        }

        return new ViewerTable
        {
            SourceFilePath = sourceFilePath,
            SheetName = sheetName,
            HasHeaderRow = hasHeader,
            Columns = columns,
            RowCount = dataRows.Count,
        };
    }

    private static ViewerColumn BuildColumn(
        IReadOnlyList<IReadOnlyList<string>> dataRows,
        int columnIndex,
        string name)
    {
        var values = new double[dataRows.Count];
        var nonEmpty = 0;
        var parsed = 0;
        for (var row = 0; row < dataRows.Count; row++)
        {
            var cell = columnIndex < dataRows[row].Count ? dataRows[row][columnIndex] : null;
            if (string.IsNullOrWhiteSpace(cell))
            {
                values[row] = double.NaN;
                continue;
            }

            nonEmpty++;
            if (NumericParsing.TryParseDouble(cell, out var value))
            {
                values[row] = value;
                parsed++;
            }
            else
            {
                values[row] = double.NaN;
            }
        }

        var isNumeric = parsed >= 2
            && nonEmpty > 0
            && parsed >= nonEmpty * NumericCellRatioThreshold;
        return new ViewerColumn { Name = name, Values = values, IsNumeric = isNumeric };
    }

    /// <summary>
    /// Treats the first row as a header when it has strictly more
    /// non-numeric (non-empty) cells than the second row — a numeric-only
    /// file keeps all rows as data, a labelled file drops row one into
    /// column names.
    /// </summary>
    private static bool DetectHeaderRow(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2)
        {
            return false;
        }

        var firstRowFailures = CountNonNumericCells(rows[0]);
        return firstRowFailures > 0 && firstRowFailures > CountNonNumericCells(rows[1]);
    }

    private static int CountNonNumericCells(IReadOnlyList<string> row)
    {
        var failures = 0;
        foreach (var cell in row)
        {
            if (!string.IsNullOrWhiteSpace(cell) && !NumericParsing.TryParseDouble(cell, out _))
            {
                failures++;
            }
        }

        return failures;
    }

    private static string GetColumnName(IReadOnlyList<string>? headerCells, int columnIndex)
    {
        if (headerCells is not null
            && columnIndex < headerCells.Count
            && !string.IsNullOrWhiteSpace(headerCells[columnIndex]))
        {
            return headerCells[columnIndex].Trim();
        }

        return $"Column {columnIndex + 1}";
    }
}
