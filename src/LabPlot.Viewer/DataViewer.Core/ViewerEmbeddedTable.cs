namespace DataViewer.Core;

/// <summary>
/// JSON-embeddable snapshot of a clipboard-pasted table. System.Text.Json
/// cannot serialise NaN, so cells are stored as nullable doubles with
/// <c>null</c> standing in for NaN / non-numeric cells.
/// </summary>
public sealed class ViewerEmbeddedTable
{
    public List<string> ColumnNames { get; set; } = new();

    public List<double?[]> Rows { get; set; } = new();

    public static ViewerEmbeddedTable FromTable(ViewerTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var embedded = new ViewerEmbeddedTable
        {
            ColumnNames = table.Columns.Select(static column => column.Name).ToList(),
        };

        for (var row = 0; row < table.RowCount; row++)
        {
            var cells = new double?[table.Columns.Count];
            for (var col = 0; col < table.Columns.Count; col++)
            {
                var value = table.Columns[col].Values[row];
                cells[col] = double.IsNaN(value) ? null : value;
            }

            embedded.Rows.Add(cells);
        }

        return embedded;
    }

    public ViewerTable ToTable()
    {
        var columnCount = ColumnNames.Count;
        var rowCount = Rows.Count;
        var columns = new ViewerColumn[columnCount];
        for (var col = 0; col < columnCount; col++)
        {
            var values = new double[rowCount];
            var finite = 0;
            for (var row = 0; row < rowCount; row++)
            {
                var cells = Rows[row];
                var cell = col < cells.Length ? cells[col] : null;
                values[row] = cell ?? double.NaN;
                if (cell.HasValue && double.IsFinite(cell.Value))
                {
                    finite++;
                }
            }

            columns[col] = new ViewerColumn
            {
                Name = col < ColumnNames.Count ? ColumnNames[col] : $"Column {col + 1}",
                Values = values,
                IsNumeric = finite >= 2,
            };
        }

        return new ViewerTable
        {
            SourceFilePath = null,
            SheetName = null,
            HasHeaderRow = true,
            Columns = columns,
            RowCount = rowCount,
        };
    }
}
