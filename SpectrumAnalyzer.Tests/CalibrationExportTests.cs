using ClosedXML.Excel;
using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class CalibrationExportTests
{
    [Fact]
    public void ToCsv_ContainsSummaryAndOneRowPerSample()
    {
        var (config, result, rows) = BuildExportFixture();
        var export = new CalibrationExport
        {
            Config = config,
            Result = result,
            Rows = rows,
        };

        var csv = export.ToCsv();

        Assert.Contains("# Quantification: Absorbance @ 280 nm", csv);
        Assert.Contains("# Path length (cm): 1", csv);
        Assert.Contains("# Fit mode: Forced origin (y = m x)", csv);
        Assert.Contains("# Unit: μM", csv);
        Assert.Contains("Dataset,Concentration,Unit,Concentration (M),Signal,Predicted,Residual,Excluded", csv);

        // One header row + 3 data rows = 3 occurrences of "sample"
        var sampleHits = csv.Split('\n').Count(line => line.StartsWith("sample"));
        Assert.Equal(3, sampleHits);
    }

    [Fact]
    public void WriteXlsx_RoundTripsHeaderAndRows()
    {
        var (config, result, rows) = BuildExportFixture();
        var export = new CalibrationExport
        {
            Config = config,
            Result = result,
            Rows = rows,
        };

        var path = Path.Combine(Path.GetTempPath(),
            $"calibration_{Guid.NewGuid():N}.xlsx");

        try
        {
            export.WriteXlsx(path);
            Assert.True(File.Exists(path));

            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Calibration");
            Assert.Equal("Quantification", sheet.Cell(3, 1).GetString());
            Assert.Equal("Absorbance @ 280 nm", sheet.Cell(3, 2).GetString());

            // Header row at row 14, first data row at 15
            Assert.Equal("Dataset", sheet.Cell(14, 1).GetString());
            Assert.Equal("sample 1", sheet.Cell(15, 1).GetString());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static (CalibrationCurveConfig Config, CalibrationResult Result, IReadOnlyList<CalibrationExportRow> Rows)
        BuildExportFixture()
    {
        var config = new CalibrationCurveConfig
        {
            Mode = CalibrationQuantificationMode.SingleWavelength,
            WavelengthNm = 280,
            PathLengthCm = 1.0,
            FitMode = CalibrationFitMode.ForceOrigin,
            ConcentrationUnit = CalibrationConcentrationUnit.MicromolPerLiter,
        };

        var inputs = new[]
        {
            new CalibrationFitInput
            {
                DatasetKey = "s1", DisplayName = "sample 1",
                ConcentrationMolar = 1e-6, Signal = 0.05,
            },
            new CalibrationFitInput
            {
                DatasetKey = "s2", DisplayName = "sample 2",
                ConcentrationMolar = 2e-6, Signal = 0.10,
            },
            new CalibrationFitInput
            {
                DatasetKey = "s3", DisplayName = "sample 3",
                ConcentrationMolar = 5e-6, Signal = 0.25,
            },
        };

        var result = CalibrationFitter.Fit(
            inputs,
            config.FitMode,
            config.Mode,
            config.PathLengthCm);

        var rows = inputs.Zip(result.Points, (input, point) => new CalibrationExportRow
        {
            DatasetName = input.DisplayName,
            ConcentrationInUnit = (input.ConcentrationMolar ?? 0) * 1e6,
            ConcentrationMolar = point.ConcentrationMolar,
            Signal = point.Signal,
            Predicted = point.Predicted,
            Residual = point.Residual,
            IsExcluded = point.IsExcluded,
        }).ToArray();

        return (config, result, rows);
    }
}
