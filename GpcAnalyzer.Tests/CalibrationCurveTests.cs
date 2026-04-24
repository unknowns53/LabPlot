using GpcAnalyzer.Core;

namespace GpcAnalyzer.Tests;

public sealed class CalibrationCurveTests
{
    [Fact]
    public void StandardCurveFileReader_LoadsSolventsAndDetectors()
    {
        var path = WriteTempFile(
            """
            {
              "Chloroform": {
                "A": { "a": -0.0005323391, "b": 0.01999486, "c": -0.5362995, "d": 9.781213 }
              },
              "DMF": {
                "A": { "a": -0.001755314, "b": 0.08524861, "c": -1.67915, "d": 16.405 },
                "B": { "a": -0.001755314, "b": 0.08524861, "c": -1.67915, "d": 16.405 }
              }
            }
            """);

        try
        {
            var curves = new StandardCurveFileReader().Read(path);

            Assert.Contains("DMF", curves.Solvents);
            Assert.Equal(new[] { "A", "B" }, curves.GetDetectors("DMF"));
            Assert.Equal("A", curves.GetCurve("dmf", "a").Detector);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CalibrationCurve_CalculatesMolecularWeightFromRetentionTime()
    {
        var coefficients = new CalibrationCurveCoefficients
        {
            A = -0.001755314,
            B = 0.08524861,
            C = -1.67915,
            D = 16.405,
        };

        var logM = coefficients.CalculateLogMolecularWeight(10);
        var molecularWeight = coefficients.CalculateMolecularWeight(10);

        Assert.Equal(6.383047, logM, 6);
        Assert.Equal(Math.Pow(10, 6.383047), molecularWeight, 0);
    }

    [Fact]
    public void MolecularWeightConverter_ConvertsDatasetXValues()
    {
        var dataset = new GpcDataset
        {
            SourceFilePath = "sample.txt",
            XLabel = "R.Time (min)",
            YLabel = "Intensity (mV)",
            Points = new[]
            {
                new GpcDataPoint { X = 10, Y = 1.2 },
                new GpcDataPoint { X = 20, Y = 2.4 },
            },
        };
        var curve = new CalibrationCurve
        {
            Solvent = "DMF",
            Detector = "A",
            Coefficients = new CalibrationCurveCoefficients
            {
                A = -0.001755314,
                B = 0.08524861,
                C = -1.67915,
                D = 16.405,
            },
        };

        var converted = new MolecularWeightConverter().Convert(dataset, curve);

        Assert.Equal("DMF", converted.Solvent);
        Assert.Equal("A", converted.Detector);
        Assert.Equal("Intensity (mV)", converted.YLabel);
        Assert.Equal(2, converted.Points.Count);
        Assert.Equal(0, converted.FilteredOutPointCount);
        Assert.Equal(20, converted.Points[0].RetentionTime, 5);
        Assert.Equal(2.4, converted.Points[0].Signal, 5);
        Assert.True(converted.Points[0].MolecularWeight < converted.Points[1].MolecularWeight);
    }

    [Fact]
    public void MolecularWeightConverter_FiltersPointsOutsideExpectedRange()
    {
        var dataset = new GpcDataset
        {
            Points = new[]
            {
                new GpcDataPoint { X = 0, Y = 1 },
                new GpcDataPoint { X = 10, Y = 2 },
                new GpcDataPoint { X = 20, Y = 3 },
                new GpcDataPoint { X = 30, Y = 4 },
            },
        };
        var curve = new CalibrationCurve
        {
            Solvent = "DMF",
            Detector = "A",
            Coefficients = new CalibrationCurveCoefficients
            {
                A = -0.001755314,
                B = 0.08524861,
                C = -1.67915,
                D = 16.405,
            },
        };

        var converted = new MolecularWeightConverter().Convert(dataset, curve);

        Assert.Equal(2, converted.Points.Count);
        Assert.Equal(2, converted.FilteredOutPointCount);
        Assert.All(converted.Points, point =>
        {
            Assert.InRange(
                point.MolecularWeight,
                MolecularWeightConverter.DefaultMinMolecularWeight,
                MolecularWeightConverter.DefaultMaxMolecularWeight);
        });
    }

    [Fact]
    public void MolecularWeightConverter_CalculatesDwdLogMAfterSortingRetentionTimeDescending()
    {
        var dataset = new GpcDataset
        {
            Points = new[]
            {
                new GpcDataPoint { X = 2, Y = 1 },
                new GpcDataPoint { X = 3, Y = 2 },
                new GpcDataPoint { X = 4, Y = 3 },
            },
        };
        var curve = new CalibrationCurve
        {
            Solvent = "Test",
            Detector = "A",
            Coefficients = new CalibrationCurveCoefficients
            {
                A = 0,
                B = 0,
                C = -1,
                D = 6,
            },
        };

        var converted = new MolecularWeightConverter().Convert(
            dataset,
            curve,
            MolecularWeightYMode.DifferentialWeightFraction);

        Assert.Equal("dw/dlogM", converted.YLabel);
        Assert.Equal(MolecularWeightYMode.DifferentialWeightFraction, converted.YMode);
        Assert.Equal(2, converted.Points.Count);
        Assert.Equal(100, converted.Points[0].MolecularWeight, 5);
        Assert.Equal(2.0 / 6.0, converted.Points[0].Signal, 5);
        Assert.Equal(1000, converted.Points[1].MolecularWeight, 5);
        Assert.Equal(1.0 / 6.0, converted.Points[1].Signal, 5);
    }

    [Fact]
    public void MolecularWeightConverter_UsesDataFileStatisticsWhenPresent()
    {
        var dataset = new GpcDataset
        {
            MolecularWeightStatistics = new MolecularWeightStatistics
            {
                Mn = 1234,
                Mw = 5678,
                Pdi = 4.6,
                Source = MolecularWeightStatisticsSource.DataFile,
            },
            Points = new[]
            {
                new GpcDataPoint { X = 2, Y = 1 },
                new GpcDataPoint { X = 3, Y = 2 },
            },
        };
        var curve = new CalibrationCurve
        {
            Solvent = "Test",
            Detector = "A",
            Coefficients = new CalibrationCurveCoefficients
            {
                A = 0,
                B = 0,
                C = 1,
                D = 0,
            },
        };

        var converted = new MolecularWeightConverter().Convert(dataset, curve);

        Assert.NotNull(converted.Statistics);
        Assert.Equal(1234, converted.Statistics.Mn);
        Assert.Equal(5678, converted.Statistics.Mw);
        Assert.Equal(4.6, converted.Statistics.Pdi);
        Assert.Equal(MolecularWeightStatisticsSource.DataFile, converted.Statistics.Source);
    }

    [Fact]
    public void MolecularWeightConverter_CalculatesStatisticsWhenDataFileStatisticsAreMissing()
    {
        var dataset = new GpcDataset
        {
            Points = new[]
            {
                new GpcDataPoint { X = 2, Y = 1 },
                new GpcDataPoint { X = 3, Y = 1 },
            },
        };
        var curve = new CalibrationCurve
        {
            Solvent = "Test",
            Detector = "A",
            Coefficients = new CalibrationCurveCoefficients
            {
                A = 0,
                B = 0,
                C = 1,
                D = 0,
            },
        };

        var converted = new MolecularWeightConverter().Convert(dataset, curve);

        Assert.NotNull(converted.Statistics);
        Assert.Equal(MolecularWeightStatisticsSource.Calculated, converted.Statistics.Source);
        Assert.Equal(181.8181818, converted.Statistics.Mn!.Value, 6);
        Assert.Equal(550, converted.Statistics.Mw!.Value, 6);
        Assert.Equal(3.025, converted.Statistics.Pdi!.Value, 6);
    }

    private static string WriteTempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
