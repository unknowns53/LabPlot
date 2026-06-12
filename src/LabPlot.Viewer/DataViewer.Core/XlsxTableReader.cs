using System.Globalization;
using ClosedXML.Excel;

namespace DataViewer.Core;

/// <summary>
/// Reads an xlsx workbook into one <see cref="ViewerTable"/> per
/// non-empty worksheet. Numeric cells are forwarded with full precision
/// via an invariant round-trip string; everything else goes through the
/// formatted display string so dates / text survive as labels.
/// </summary>
public sealed class XlsxTableReader : IViewerDataReader
{
    public ViewerTableSet Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Workbook was not found.", filePath);
        }

        using var workbook = new XLWorkbook(filePath);
        var tables = new List<ViewerTable>();
        foreach (var sheet in workbook.Worksheets)
        {
            var range = sheet.RangeUsed();
            if (range is null)
            {
                continue;
            }

            var rows = ReadCellRows(range);
            try
            {
                tables.Add(TableBuilder.Build(rows, filePath, sheet.Name));
            }
            catch (InvalidDataException)
            {
                // Sheets without any usable rows (e.g. formatting-only)
                // are skipped instead of failing the whole workbook.
            }
        }

        if (tables.Count == 0)
        {
            throw new InvalidDataException("The workbook does not contain any data sheets.");
        }

        return new ViewerTableSet { Tables = tables };
    }

    private static List<IReadOnlyList<string>> ReadCellRows(IXLRange range)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in range.Rows())
        {
            var cells = new List<string>();
            foreach (var cell in row.Cells(1, range.ColumnCount()))
            {
                cells.Add(FormatCell(cell));
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static string FormatCell(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number)
        {
            return cell.GetValue<double>().ToString("R", CultureInfo.InvariantCulture);
        }

        return cell.GetFormattedString();
    }
}
