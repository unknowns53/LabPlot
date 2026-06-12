using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class ColumnMappingInferenceTests
{
    private static ViewerTable BuildTable(params (string Name, double[] Values, bool IsNumeric)[] columns)
    {
        return new ViewerTable
        {
            Columns = columns
                .Select(static column => new ViewerColumn
                {
                    Name = column.Name,
                    Values = column.Values,
                    IsNumeric = column.IsNumeric,
                })
                .ToArray(),
            RowCount = columns.Length > 0 ? columns[0].Values.Length : 0,
        };
    }

    private static readonly double[] SampleValues = { 1, 2, 3 };

    [Fact]
    public void Infer_FirstNumericBecomesX_RestBecomeY()
    {
        var table = BuildTable(
            ("X", SampleValues, true),
            ("Y1", SampleValues, true),
            ("Y2", SampleValues, true));

        var mapping = ColumnMappingInference.Infer(table);

        Assert.Equal(0, mapping.XColumnIndex);
        Assert.Equal(new[] { 1, 2 }, mapping.YColumnIndexes);
    }

    [Fact]
    public void Infer_TextFirstColumn_SkipsToFirstNumericForX()
    {
        var table = BuildTable(
            ("Label", SampleValues, false),
            ("X", SampleValues, true),
            ("Y", SampleValues, true));

        var mapping = ColumnMappingInference.Infer(table);

        Assert.Equal(1, mapping.XColumnIndex);
        Assert.Equal(new[] { 2 }, mapping.YColumnIndexes);
    }

    [Fact]
    public void Infer_WideTable_CapsAutoSeriesAtLimit()
    {
        var columns = Enumerable.Range(0, 12)
            .Select(static i => ($"C{i}", SampleValues, true))
            .ToArray();

        var mapping = ColumnMappingInference.Infer(BuildTable(columns));

        Assert.Equal(ColumnMappingInference.MaxAutoSeriesCount, mapping.YColumnIndexes.Count);
        Assert.Equal(Enumerable.Range(1, 8), mapping.YColumnIndexes);
    }

    [Fact]
    public void Infer_SingleNumericColumn_PlotsItAgainstItself()
    {
        var table = BuildTable(
            ("Label", SampleValues, false),
            ("Value", SampleValues, true));

        var mapping = ColumnMappingInference.Infer(table);

        Assert.Equal(1, mapping.XColumnIndex);
        Assert.Equal(new[] { 1 }, mapping.YColumnIndexes);
    }

    [Fact]
    public void Infer_NoNumericColumns_Throws()
    {
        var table = BuildTable(("A", SampleValues, false), ("B", SampleValues, false));

        Assert.Throws<InvalidDataException>(() => ColumnMappingInference.Infer(table));
    }
}
