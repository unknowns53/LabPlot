using ClosedXML.Excel;
using DlsAnalyzer.Core;
using LabPlot.Core;

namespace DlsAnalyzer.Tests;

public sealed class DlsAnalysisExporterTests
{
    [Fact]
    public void Csv_WritesGeneratorHeader_SummaryAndDataSections()
    {
        var data = CreateSampleExport();
        var text = new DlsCsvAnalysisExporter().ToText(data);

        Assert.Contains("# LabPlot DLS analysis export", text);
        Assert.Contains("# Summary", text);
        Assert.Contains("# Data (1-41_2_20 / Number)", text);
        Assert.Contains("Size (d.nm),Number (%)", text);
        Assert.Contains("0.5,2.1", text);
        // Cumulant + Stokes-Einstein + metadata columns surface.
        Assert.Contains("100", text); // Hydrodynamic diameter
        Assert.Contains("Water", text); // Solvent
    }

    [Fact]
    public void Csv_OmitsCumulantColumns_WhenAnalysisAbsent()
    {
        var data = new AnalysisExport
        {
            Entries = new[]
            {
                new DlsAnalysisExportEntry
                {
                    DisplayName = "Sheet1",
                    DistributionMode = "Number",
                    XLabel = "Size (d.nm)",
                    YLabel = "Number (%)",
                    Xs = new[] { 1.0, 10.0, 100.0 },
                    Ys = new[] { 0.5, 1.2, 0.3 },
                },
            },
            GeneratorName = "LabPlot DLS",
            CreatedAt = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        };

        var text = new DlsCsvAnalysisExporter().ToText(data);

        Assert.Contains("# Summary", text);
        // Empty cumulant columns yield consecutive commas.
        Assert.Contains(",,,,", text);
    }

    [Fact]
    public void Xlsx_WritesSummaryAndPerSheetData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            new DlsXlsxAnalysisExporter().Export(CreateSampleExport(), path);

            using var workbook = new XLWorkbook(path);
            Assert.Contains(workbook.Worksheets, w => w.Name == "Summary");

            var summary = workbook.Worksheet("Summary");
            Assert.Equal("Sheet", summary.Cell(4, 1).GetString());
            Assert.Equal("1-41_2_20", summary.Cell(5, 1).GetString());
            Assert.Equal("Number", summary.Cell(5, 2).GetString());
            Assert.Equal(25.0, summary.Cell(5, 3).GetDouble()); // Temperature
            Assert.Equal("Water", summary.Cell(5, 4).GetString());
            Assert.Equal(100.0, summary.Cell(5, 10).GetDouble()); // Z-average

            var data = workbook.Worksheet("1-41_2_20");
            Assert.Equal("Sheet", data.Cell(1, 1).GetString());
            Assert.Equal("1-41_2_20", data.Cell(1, 2).GetString());
            Assert.Equal("Size (d.nm)", data.Cell(4, 1).GetString());
            Assert.Equal(0.5, data.Cell(5, 1).GetDouble());
            Assert.Equal(2.1, data.Cell(5, 2).GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xlsx_DeduplicatesSheetNames()
    {
        var entry1 = MakeBasicEntry("Sample");
        var entry2 = MakeBasicEntry("Sample");

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            new DlsXlsxAnalysisExporter().Export(new AnalysisExport
            {
                Entries = new[] { entry1, entry2 },
                GeneratorName = "LabPlot DLS",
            }, path);

            using var workbook = new XLWorkbook(path);
            Assert.Contains(workbook.Worksheets, w => w.Name == "Sample");
            Assert.Contains(workbook.Worksheets, w => w.Name == "Sample (2)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xlsx_StillProducesSummarySheet_WhenNoEntries()
    {
        // Summary always renders (generator + timestamp + headers) so
        // the "Empty" fallback in the exporter is dead code in practice.
        // Lock that contract here so future refactors that drop the
        // unconditional Summary sheet trip a clear failure.
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            var data = new AnalysisExport
            {
                Entries = Array.Empty<AnalysisExportEntry>(),
                GeneratorName = "LabPlot DLS",
            };
            new DlsXlsxAnalysisExporter().Export(data, path);

            using var workbook = new XLWorkbook(path);
            Assert.Contains(workbook.Worksheets, w => w.Name == "Summary");
            // No data sheets to follow.
            Assert.Equal(1, workbook.Worksheets.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AnalysisExport CreateSampleExport()
    {
        var entry = new DlsAnalysisExportEntry
        {
            DisplayName = "1-41_2_20",
            SourceFilePath = "test_file.xlsx",
            DistributionMode = "Number",
            XLabel = "Size (d.nm)",
            YLabel = "Number (%)",
            Xs = new[] { 0.5, 1.0, 5.0, 10.0 },
            Ys = new[] { 2.1, 5.4, 12.0, 8.7 },
            TemperatureCelsius = 25.0,
            Solvent = "Water",
            ConcentrationMgPerMl = 1.0,
            RefractiveIndex = 1.331,
            ViscosityMpas = 0.89,
            WavelengthNm = 633.0,
            ScatteringAngleDegrees = 173.0,
            HydrodynamicDiameterNm = 100.0,
            Cumulant = new CumulantResult
            {
                FirstCumulantPerMicrosecond = 0.0034,
                SecondCumulantPerMicrosecondSquared = 1e-7,
                PolydispersityIndex = 0.087,
                RSquared = 0.999,
                AppliedRangeMinMicroseconds = 1.0,
                AppliedRangeMaxMicroseconds = 200.0,
                PointCount = 24,
            },
        };

        return new AnalysisExport
        {
            Entries = new[] { entry },
            GeneratorName = "LabPlot DLS",
            CreatedAt = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero),
        };
    }

    private static DlsAnalysisExportEntry MakeBasicEntry(string name)
        => new()
        {
            DisplayName = name,
            DistributionMode = "Number",
            XLabel = "Size (d.nm)",
            YLabel = "Number (%)",
            Xs = new[] { 1.0 },
            Ys = new[] { 1.0 },
        };
}
