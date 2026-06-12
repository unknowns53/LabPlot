namespace DataViewer.Core;

/// <summary>
/// Result of the automatic X / Y column assignment for one table.
/// </summary>
public sealed record ColumnMapping
{
    public required int XColumnIndex { get; init; }

    /// <summary>
    /// Numeric columns auto-enabled as Y series. Capped at
    /// <see cref="ColumnMappingInference.MaxAutoSeriesCount"/>; the user can
    /// enable further columns manually from the mapping panel.
    /// </summary>
    public required IReadOnlyList<int> YColumnIndexes { get; init; }
}

/// <summary>
/// Infers the default column mapping for a freshly loaded table:
/// first numeric column becomes X, the remaining numeric columns become
/// Y series (up to a cap so wide instrument dumps stay responsive).
/// </summary>
public static class ColumnMappingInference
{
    public const int MaxAutoSeriesCount = 8;

    public static ColumnMapping Infer(ViewerTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var numericIndexes = new List<int>();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].IsNumeric)
            {
                numericIndexes.Add(i);
            }
        }

        if (numericIndexes.Count == 0)
        {
            throw new InvalidDataException("The table does not contain any numeric columns to plot.");
        }

        var xIndex = numericIndexes[0];
        var yIndexes = numericIndexes
            .Skip(1)
            .Take(MaxAutoSeriesCount)
            .ToArray();

        // 数値 1 列だけのテーブルは X 自身を Y として描く (行番号 X は B3 以降の課題)
        if (yIndexes.Length == 0)
        {
            yIndexes = new[] { xIndex };
        }

        return new ColumnMapping
        {
            XColumnIndex = xIndex,
            YColumnIndexes = yIndexes,
        };
    }
}
