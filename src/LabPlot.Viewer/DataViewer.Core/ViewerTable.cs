namespace DataViewer.Core;

/// <summary>
/// One column of a loaded table. <see cref="Values"/> always has one slot
/// per data row; cells that were empty or failed numeric parsing hold
/// <see cref="double.NaN"/> so columns stay rectangular regardless of the
/// source quality.
/// </summary>
public sealed record ViewerColumn
{
    public required string Name { get; init; }

    public required double[] Values { get; init; }

    /// <summary>
    /// True when at least 80% of the non-empty cells parsed as finite
    /// numbers (and at least two finite values exist). Non-numeric columns
    /// are kept so the column picker can show them greyed out.
    /// </summary>
    public required bool IsNumeric { get; init; }
}

/// <summary>
/// One rectangular table loaded from a file, an xlsx worksheet, or a
/// clipboard paste. The viewer maps one table to one dataset entry whose
/// columns become plottable series.
/// </summary>
public sealed record ViewerTable
{
    /// <summary>Null for clipboard-pasted tables.</summary>
    public string? SourceFilePath { get; init; }

    /// <summary>Set only for tables read from an xlsx worksheet.</summary>
    public string? SheetName { get; init; }

    /// <summary>True when the first source row was consumed as column names.</summary>
    public bool HasHeaderRow { get; init; }

    public required IReadOnlyList<ViewerColumn> Columns { get; init; }

    public int RowCount { get; init; }
}

/// <summary>
/// Reader output container: delimited text yields exactly one table, an
/// xlsx workbook yields one table per non-empty worksheet.
/// </summary>
public sealed record ViewerTableSet
{
    public required IReadOnlyList<ViewerTable> Tables { get; init; }
}
