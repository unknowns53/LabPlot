using GpcAnalyzer.Core;

namespace GpcAnalyzer.Tests;

public sealed class CsvGpcDataReaderTests
{
    [Fact]
    public void Read_LoadsFirstTwoCsvColumns()
    {
        var path = WriteTempFile(
            """
            Time,Signal,Ignored
            0.00,0.012,a
            0.01,0.013,b
            """);

        try
        {
            var dataset = new CsvGpcDataReader().Read(path);

            Assert.Equal(2, dataset.Points.Count);
            Assert.Equal("Time", dataset.XLabel);
            Assert.Equal("Signal", dataset.YLabel);
            Assert.Equal(0.01, dataset.Points[1].X, 5);
            Assert.Equal(0.013, dataset.Points[1].Y, 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_SkipsRowsThatCannotBeParsed()
    {
        var path = WriteTempFile(
            """
            RetentionTime,Intensity
            10.0,123
            invalid,999
            10.1
            10.2,140
            10.3,not-number
            """);

        try
        {
            var dataset = new CsvGpcDataReader().Read(path);

            Assert.Equal(2, dataset.Points.Count);
            Assert.Equal(10.0, dataset.Points[0].X, 5);
            Assert.Equal(140, dataset.Points[1].Y, 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ThrowsWhenNoValidRowsExist()
    {
        var path = WriteTempFile(
            """
            Time,Signal
            abc,def
            """);

        try
        {
            Assert.Throws<InvalidDataException>(() => new CsvGpcDataReader().Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_LoadsLabSolutionsChromatogramSection()
    {
        var path = WriteTempFile(
            """
            [Header]
            Application Name	LabSolutions

            [LC Chromatogram(Detector A-Ch1)]
            Intensity Units	mV
            Intensity Multiplier	0.001
            R.Time (min)	Intensity
            0.00000	-961
            0.00833	-114
            0.01667	2

            [LC Chromatogram(Detector B-Ch1)]
            R.Time (min)	Intensity
            0.00000	999
            """);

        try
        {
            var dataset = new CsvGpcDataReader().Read(path);

            Assert.Equal(3, dataset.Points.Count);
            Assert.Equal("R.Time (min)", dataset.XLabel);
            Assert.Equal("Intensity (mV)", dataset.YLabel);
            Assert.Equal(-0.961, dataset.Points[0].Y, 5);
            Assert.Equal(0.01667, dataset.Points[2].X, 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, contents);
        return path;
    }
}
