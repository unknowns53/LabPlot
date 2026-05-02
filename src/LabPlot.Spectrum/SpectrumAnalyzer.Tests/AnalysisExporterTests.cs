using ClosedXML.Excel;
using LabPlot.Core;
using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class AnalysisExporterTests
{
    [Fact]
    public void Csv_WritesGeneratorHeaderAndSpectrumRows()
    {
        var data = CreateSampleExport();
        var text = new CsvAnalysisExporter().ToText(data);

        Assert.Contains("# Spectrum Visualization analysis export", text);
        Assert.Contains("# Spectrum (Sample.txt)", text);
        Assert.Contains("Wavelength / nm,Absorbance", text);
        Assert.Contains("200,0.5", text);
        Assert.Contains("400,1.2", text);
    }

    [Fact]
    public void Xlsx_WritesSpectrumSheet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            new XlsxAnalysisExporter().Export(CreateSampleExport(), path);

            using var workbook = new XLWorkbook(path);
            Assert.Contains(workbook.Worksheets, w => w.Name == "Spectrum");

            var sheet = workbook.Worksheet("Spectrum");
            Assert.Equal("Sample.txt", sheet.Cell(4, 1).GetString());
            Assert.Equal("Wavelength / nm", sheet.Cell(5, 1).GetString());
            Assert.Equal("Absorbance", sheet.Cell(5, 2).GetString());
            Assert.Equal(200, sheet.Cell(6, 1).GetDouble());
            Assert.Equal(0.5, sheet.Cell(6, 2).GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xlsx_FallsBackToEmptySheetWhenNoData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            var data = new AnalysisExport
            {
                Entries = Array.Empty<AnalysisExportEntry>(),
                GeneratorName = "Spectrum Visualization",
            };
            new XlsxAnalysisExporter().Export(data, path);

            using var workbook = new XLWorkbook(path);
            Assert.Contains(workbook.Worksheets, w => w.Name == "Empty");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AnalysisExport CreateSampleExport()
    {
        var entry = new SpectrumAnalysisExportEntry
        {
            DisplayName = "Sample.txt",
            SourceFilePath = "Sample.txt",
            XLabel = "Wavelength / nm",
            YLabel = "Absorbance",
            Points = new[]
            {
                new SpectrumDataPoint { X = 200, Y = 0.5 },
                new SpectrumDataPoint { X = 300, Y = 0.9 },
                new SpectrumDataPoint { X = 400, Y = 1.2 },
            },
        };

        return new AnalysisExport
        {
            Entries = new[] { entry },
            GeneratorName = "Spectrum Visualization",
            CreatedAt = new DateTimeOffset(2026, 4, 30, 12, 34, 56, TimeSpan.Zero),
        };
    }
}
