using ClosedXML.Excel;
using GpcAnalyzer.Core;

namespace GpcAnalyzer.Tests;

public sealed class AnalysisExporterTests
{
    [Fact]
    public void Csv_WritesAllSectionsWithExpectedValues()
    {
        var data = CreateSampleExport();
        var text = new CsvAnalysisExporter().ToText(data);

        Assert.Contains("# Statistics", text);
        Assert.Contains("# Peak List", text);
        Assert.Contains("# Chromatogram (Sample.txt, Detector A)", text);
        Assert.Contains("# Molecular Weight (Sample.txt, Detector A)", text);

        Assert.Contains("Sample.txt,A,6277,6851,1.0914,DataFile,auto", text);
        Assert.Contains("Sample.txt,A,1,6277,6851,1.09,16.14", text);
        Assert.Contains("Sample.txt,A,2,2165,2480,1.14,17.53", text);
        Assert.Contains("Dispersity (Ð)", text);
    }

    [Fact]
    public void Csv_PicksDwOverDlogMHeaderForDifferentialMode()
    {
        var data = CreateSampleExport(MolecularWeightYMode.DifferentialWeightFraction);
        var text = new CsvAnalysisExporter().ToText(data);

        Assert.Contains("RetentionTime,MolecularWeight,log10(M),dw/dlogM", text);
    }

    [Fact]
    public void Xlsx_WritesAllSheets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            new XlsxAnalysisExporter().Export(CreateSampleExport(), path);

            using var workbook = new XLWorkbook(path);
            Assert.Contains(workbook.Worksheets, w => w.Name == "Statistics");
            Assert.Contains(workbook.Worksheets, w => w.Name == "Peak List");
            Assert.Contains(workbook.Worksheets, w => w.Name == "Chromatogram");
            Assert.Contains(workbook.Worksheets, w => w.Name == "Molecular Weight");

            var stats = workbook.Worksheet("Statistics");
            Assert.Equal("Dispersity (Ð)", stats.Cell(4, 5).GetString());
            Assert.Equal("Sample.txt", stats.Cell(5, 1).GetString());
            Assert.Equal(6277, stats.Cell(5, 3).GetDouble());
            Assert.Equal(6851, stats.Cell(5, 4).GetDouble());
            Assert.Equal(1.0914, stats.Cell(5, 5).GetDouble());

            var peaks = workbook.Worksheet("Peak List");
            Assert.Equal("1", peaks.Cell(2, 3).GetString());
            Assert.Equal(16.14, peaks.Cell(2, 7).GetDouble());

            var chromatogram = workbook.Worksheet("Chromatogram");
            Assert.Equal("R.Time (min)", chromatogram.Cell(2, 1).GetString());
            Assert.Equal("Intensity (mV)", chromatogram.Cell(2, 2).GetString());
            Assert.Equal(0.5, chromatogram.Cell(4, 1).GetDouble());
            Assert.Equal(100, chromatogram.Cell(4, 2).GetDouble());

            var mw = workbook.Worksheet("Molecular Weight");
            Assert.Equal("RetentionTime", mw.Cell(2, 1).GetString());
            Assert.Equal("MolecularWeight", mw.Cell(2, 2).GetString());
            Assert.Equal("log10(M)", mw.Cell(2, 3).GetString());
            Assert.Equal("Signal", mw.Cell(2, 4).GetString());
            Assert.Equal(1000, mw.Cell(3, 2).GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xlsx_OmitsMolecularWeightSheetWhenAbsent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            var data = CreateSampleExport(includeMolecularWeight: false);
            new XlsxAnalysisExporter().Export(data, path);

            using var workbook = new XLWorkbook(path);
            Assert.DoesNotContain(workbook.Worksheets, w => w.Name == "Molecular Weight");
            Assert.Contains(workbook.Worksheets, w => w.Name == "Chromatogram");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AnalysisExport CreateSampleExport(
        MolecularWeightYMode yMode = MolecularWeightYMode.Signal,
        bool includeMolecularWeight = true)
    {
        var statistics = new MolecularWeightStatistics
        {
            Mn = 6277,
            Mw = 6851,
            Pdi = 1.0914,
            Source = MolecularWeightStatisticsSource.DataFile,
            Peaks = new List<MolecularWeightPeak>
            {
                new() { PeakId = "1", Mn = 6277, Mw = 6851, Pdi = 1.09, Percent = 16.14 },
                new() { PeakId = "2", Mn = 2165, Mw = 2480, Pdi = 1.14, Percent = 17.53 },
            },
            SelectedPeakId = null,
        };

        MolecularWeightDataset? mwDataset = null;
        if (includeMolecularWeight)
        {
            mwDataset = new MolecularWeightDataset
            {
                Solvent = "DMF",
                Detector = "A",
                YMode = yMode,
                Points = new[]
                {
                    new MolecularWeightDataPoint
                    {
                        RetentionTime = 0.5,
                        MolecularWeight = 1000,
                        LogMolecularWeight = 3.0,
                        Signal = 50,
                    },
                    new MolecularWeightDataPoint
                    {
                        RetentionTime = 1.0,
                        MolecularWeight = 500,
                        LogMolecularWeight = 2.69897,
                        Signal = 75,
                    },
                },
            };
        }

        var entry = new AnalysisExportEntry
        {
            DisplayName = "Sample.txt",
            SourceFilePath = "Sample.txt",
            Detector = "A",
            XLabel = "R.Time (min)",
            YLabel = "Intensity (mV)",
            ChromatogramPoints = new[]
            {
                new GpcDataPoint { X = 0.0, Y = -1.0 },
                new GpcDataPoint { X = 0.5, Y = 100 },
                new GpcDataPoint { X = 1.0, Y = 50 },
            },
            Statistics = statistics,
            MolecularWeightDataset = mwDataset,
        };

        return new AnalysisExport
        {
            Entries = new[] { entry },
            CreatedAt = new DateTimeOffset(2026, 4, 30, 12, 34, 56, TimeSpan.Zero),
        };
    }
}
