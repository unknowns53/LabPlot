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

            [Average Molecular Weight Table(Detector A)]
            # of Peaks	2
            Peak#	Mn	Mw	Mz	Mz1	Mv	Mw/Mn	Mv/Mn	Mz/Mw	I.Visc	%
            Total	0	9999	0	0	0	999	0	0	1	100
            1	100	200	0	0	0	2	0	0	1	20
            2	300	450	0	0	0	1.5	0	0	1	80

            [Average Molecular Weight Table(Detector B)]
            # of Peaks	2
            Peak#	Mn	Mw	Mz	Mz1	Mv	Mw/Mn	Mv/Mn	Mz/Mw	I.Visc	%
            Total	0	9999	0	0	0	999	0	0	1	100
            1	30	60	0	0	0	2	0	0	1	70
            2	10	12	0	0	0	1.2	0	0	1	30

            [LC Chromatogram(Detector A-Ch1)]
            Intensity Units	mV
            Intensity Multiplier	0.001
            R.Time (min)	Intensity
            0.00000	-961
            0.00833	-114
            0.01667	2

            [LC Chromatogram(Detector B-Ch1)]
            Intensity Units	mV
            Intensity Multiplier	0.001
            R.Time (min)	Intensity
            0.00000	999
            0.00833	1000
            """);

        try
        {
            var dataset = new CsvGpcDataReader().Read(path);

            Assert.Equal("A", dataset.Detector);
            Assert.Equal(new[] { "A", "B" }, dataset.AvailableDetectors);
            Assert.Equal(3, dataset.Points.Count);
            Assert.Equal("R.Time (min)", dataset.XLabel);
            Assert.Equal("Intensity (mV)", dataset.YLabel);
            Assert.Equal(-0.961, dataset.Points[0].Y, 5);
            Assert.Equal(0.01667, dataset.Points[2].X, 5);
            Assert.NotNull(dataset.MolecularWeightStatistics);
            Assert.Equal(300, dataset.MolecularWeightStatistics.Mn);
            Assert.Equal(450, dataset.MolecularWeightStatistics.Mw);
            Assert.Equal(1.5, dataset.MolecularWeightStatistics.Pdi);
            Assert.Equal(MolecularWeightStatisticsSource.DataFile, dataset.MolecularWeightStatistics.Source);
            Assert.Equal(new[] { "2", "1" }, dataset.MolecularWeightStatistics.Peaks.Select(peak => peak.PeakId));
            Assert.All(dataset.MolecularWeightStatistics.Peaks, peak => Assert.NotEqual("Total", peak.PeakId));

            var detectorB = dataset.WithDetector("B");
            Assert.Equal("B", detectorB.Detector);
            Assert.Equal(2, detectorB.Points.Count);
            Assert.Equal(0.999, detectorB.Points[0].Y, 5);
            Assert.Equal(1.000, detectorB.Points[1].Y, 5);
            Assert.NotNull(detectorB.MolecularWeightStatistics);
            Assert.Equal(30, detectorB.MolecularWeightStatistics.Mn);
            Assert.Equal(60, detectorB.MolecularWeightStatistics.Mw);
            Assert.Equal(2, detectorB.MolecularWeightStatistics.Pdi);
            Assert.Equal(new[] { "1", "2" }, detectorB.MolecularWeightStatistics.Peaks.Select(peak => peak.PeakId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_IgnoresLabSolutionsTotalMolecularWeightRow()
    {
        var path = WriteTempFile(
            """
            [Header]
            Application Name	LabSolutions

            [Average Molecular Weight Table(Detector A)]
            # of Peaks	1
            Peak#	Mn	Mw	Mz	Mz1	Mv	Mw/Mn	Mv/Mn	Mz/Mw	I.Visc	%
            Total	0	9895	18505	23724	0	45796.05478	0.00000	1.87026	1.00000	100.0000

            [LC Chromatogram(Detector A-Ch1)]
            R.Time (min)	Intensity
            0.00000	1
            0.00833	2
            """);

        try
        {
            var dataset = new CsvGpcDataReader().Read(path);

            Assert.Null(dataset.MolecularWeightStatistics);
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
