using System.Text;
using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class ClipboardTableParserTests
{
    [Fact]
    public void Parse_TabSeparatedExcelPaste_BuildsTable()
    {
        var table = ClipboardTableParser.Parse("Temp\tAbs\r\n25\t0.12\r\n30\t0.45\r\n");

        Assert.True(table.HasHeaderRow);
        Assert.Null(table.SourceFilePath);
        Assert.Equal(new[] { 25.0, 30.0 }, table.Columns[0].Values);
        Assert.Equal(new[] { 0.12, 0.45 }, table.Columns[1].Values);
    }

    [Fact]
    public void Parse_CommaSeparatedText_FallsBackToDelimiterGuess()
    {
        var table = ClipboardTableParser.Parse("1,10\n2,20\n");

        Assert.False(table.HasHeaderRow);
        Assert.Equal(2, table.Columns.Count);
    }

    [Fact]
    public void Parse_EmptyText_Throws()
    {
        Assert.Throws<InvalidDataException>(static () => ClipboardTableParser.Parse("   "));
        Assert.Throws<InvalidDataException>(static () => ClipboardTableParser.Parse(null));
    }

    [Fact]
    public void Parse_TableOverCellLimit_Throws()
    {
        // 2 列 × (上限/2 + 1) 行 > MaxCellCount のテキストを合成
        var rows = ClipboardTableParser.MaxCellCount / 2 + 1;
        var builder = new StringBuilder(rows * 8);
        for (var i = 0; i < rows; i++)
        {
            builder.Append(i).Append('\t').Append(i).Append('\n');
        }

        Assert.Throws<InvalidDataException>(() => ClipboardTableParser.Parse(builder.ToString()));
    }
}
