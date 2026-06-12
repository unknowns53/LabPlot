using ClosedXML.Excel;
using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class XlsxTableReaderTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("viewer-xlsx-tests").FullName;

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

    private string CreateWorkbook(Action<XLWorkbook> populate)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".xlsx");
        using var workbook = new XLWorkbook();
        populate(workbook);
        workbook.SaveAs(path);
        return path;
    }

    [Fact]
    public void Read_SingleSheet_BuildsTableWithSheetName()
    {
        var path = CreateWorkbook(static workbook =>
        {
            var sheet = workbook.AddWorksheet("RunA");
            sheet.Cell(1, 1).Value = "Time";
            sheet.Cell(1, 2).Value = "Signal";
            sheet.Cell(2, 1).Value = 0.5;
            sheet.Cell(2, 2).Value = 1.25;
            sheet.Cell(3, 1).Value = 1.0;
            sheet.Cell(3, 2).Value = 2.5;
        });

        var set = new XlsxTableReader().Read(path);
        var table = Assert.Single(set.Tables);

        Assert.Equal("RunA", table.SheetName);
        Assert.Equal(path, table.SourceFilePath);
        Assert.True(table.HasHeaderRow);
        Assert.Equal(new[] { 0.5, 1.0 }, table.Columns[0].Values);
        Assert.Equal(new[] { 1.25, 2.5 }, table.Columns[1].Values);
    }

    [Fact]
    public void Read_MultipleSheets_YieldsOneTablePerNonEmptySheet()
    {
        var path = CreateWorkbook(static workbook =>
        {
            var first = workbook.AddWorksheet("First");
            first.Cell(1, 1).Value = "X";
            first.Cell(1, 2).Value = "Y";
            first.Cell(2, 1).Value = 1;
            first.Cell(2, 2).Value = 2;
            first.Cell(3, 1).Value = 3;
            first.Cell(3, 2).Value = 4;

            workbook.AddWorksheet("EmptySheet");

            var third = workbook.AddWorksheet("Third");
            third.Cell(1, 1).Value = 10;
            third.Cell(1, 2).Value = 20;
            third.Cell(2, 1).Value = 30;
            third.Cell(2, 2).Value = 40;
        });

        var set = new XlsxTableReader().Read(path);

        Assert.Equal(2, set.Tables.Count);
        Assert.Equal(new[] { "First", "Third" }, set.Tables.Select(static table => table.SheetName));
        Assert.False(set.Tables[1].HasHeaderRow);
    }

    [Fact]
    public void Read_MixedTextColumn_IsNotNumeric()
    {
        var path = CreateWorkbook(static workbook =>
        {
            var sheet = workbook.AddWorksheet("Mixed");
            sheet.Cell(1, 1).Value = "Label";
            sheet.Cell(1, 2).Value = "Value";
            sheet.Cell(2, 1).Value = "run-1";
            sheet.Cell(2, 2).Value = 1.5;
            sheet.Cell(3, 1).Value = "run-2";
            sheet.Cell(3, 2).Value = 2.5;
        });

        var table = Assert.Single(new XlsxTableReader().Read(path).Tables);

        Assert.False(table.Columns[0].IsNumeric);
        Assert.True(table.Columns[1].IsNumeric);
    }

    [Fact]
    public void Read_WorkbookWithoutData_Throws()
    {
        var path = CreateWorkbook(static workbook => workbook.AddWorksheet("Nothing"));

        Assert.Throws<InvalidDataException>(() => new XlsxTableReader().Read(path));
    }
}
