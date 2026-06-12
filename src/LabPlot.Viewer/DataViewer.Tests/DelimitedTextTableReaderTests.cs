using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class DelimitedTextTableReaderTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("viewer-csv-tests").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteTempFile(string content, string extension = ".csv")
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, content);
        return path;
    }

    private ViewerTable ReadSingle(string content, string extension = ".csv")
    {
        var reader = new DelimitedTextTableReader();
        var set = reader.Read(WriteTempFile(content, extension));
        return Assert.Single(set.Tables);
    }

    [Fact]
    public void Read_CommaCsvWithHeader_BuildsNamedNumericColumns()
    {
        var table = ReadSingle("Temp,Transmittance\n25,99.1\n30,98.5\n35,12.3\n");

        Assert.True(table.HasHeaderRow);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("Temp", table.Columns[0].Name);
        Assert.Equal("Transmittance", table.Columns[1].Name);
        Assert.All(table.Columns, static column => Assert.True(column.IsNumeric));
        Assert.Equal(3, table.RowCount);
        Assert.Equal(new[] { 25.0, 30.0, 35.0 }, table.Columns[0].Values);
    }

    [Fact]
    public void Read_TabSeparated_GuessesTabDelimiter()
    {
        var table = ReadSingle("X\tY1\tY2\n1\t10\t100\n2\t20\t200\n", ".tsv");

        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(new[] { 100.0, 200.0 }, table.Columns[2].Values);
    }

    [Fact]
    public void Read_SemicolonSeparated_GuessesSemicolonDelimiter()
    {
        var table = ReadSingle("X;Y\n1;10\n2;20\n");

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(new[] { 10.0, 20.0 }, table.Columns[1].Values);
    }

    [Fact]
    public void Read_WhitespaceSeparated_FallsBackToTokenizer()
    {
        var table = ReadSingle("X  Y\n1   10\n2\t 20\n", ".txt");

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(new[] { 1.0, 2.0 }, table.Columns[0].Values);
    }

    [Fact]
    public void Read_NumericOnlyFile_HasNoHeaderRow()
    {
        var table = ReadSingle("1,10\n2,20\n3,30\n");

        Assert.False(table.HasHeaderRow);
        Assert.Equal("Column 1", table.Columns[0].Name);
        Assert.Equal(3, table.RowCount);
    }

    [Fact]
    public void Read_DecimalCommaCells_ParseViaFallback()
    {
        var table = ReadSingle("X\tY\n1\t0,00833\n2\t0,5\n", ".tsv");

        Assert.Equal(new[] { 0.00833, 0.5 }, table.Columns[1].Values);
    }

    [Fact]
    public void Read_EmptyCells_BecomeNaN()
    {
        var table = ReadSingle("X,Y\n1,\n2,20\n3,30\n");

        Assert.True(double.IsNaN(table.Columns[1].Values[0]));
        Assert.Equal(20.0, table.Columns[1].Values[1]);
        // 2/3 のセルだけ数値でも、非空セル基準では 2/2 なので numeric のまま
        Assert.True(table.Columns[1].IsNumeric);
    }

    [Fact]
    public void Read_Utf8Bom_DoesNotLeakIntoColumnName()
    {
        var path = Path.Combine(_tempDir, "bom.csv");
        File.WriteAllText(path, "X,Y\n1,10\n2,20\n", new System.Text.UTF8Encoding(true));

        var set = new DelimitedTextTableReader().Read(path);
        Assert.Equal("X", set.Tables[0].Columns[0].Name);
    }

    [Fact]
    public void Read_QuotedFieldWithEmbeddedComma_StaysOneCell()
    {
        var table = ReadSingle("\"Sample, run 1\",Y\nabc,10\ndef,20\n");

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("Sample, run 1", table.Columns[0].Name);
        Assert.False(table.Columns[0].IsNumeric);
        Assert.True(table.Columns[1].IsNumeric);
    }

    [Fact]
    public void Read_TextColumn_IsNotNumeric()
    {
        var table = ReadSingle("Name,Value\nsample-a,1\nsample-b,2\nsample-c,3\n");

        Assert.False(table.Columns[0].IsNumeric);
        Assert.True(table.Columns[1].IsNumeric);
    }

    [Fact]
    public void Read_RaggedRows_ArePaddedWithNaN()
    {
        var table = ReadSingle("X,Y,Z\n1,10,100\n2,20\n");

        Assert.Equal(3, table.Columns.Count);
        Assert.True(double.IsNaN(table.Columns[2].Values[1]));
    }

    [Fact]
    public void Read_EmptyFile_Throws()
    {
        var path = WriteTempFile("\n\n");
        Assert.Throws<InvalidDataException>(() => new DelimitedTextTableReader().Read(path));
    }

    [Fact]
    public void Read_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => new DelimitedTextTableReader().Read(Path.Combine(_tempDir, "missing.csv")));
    }
}
