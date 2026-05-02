using DlsAnalyzer.Core;
using DlsAnalyzer.Tests.Fixtures;

namespace DlsAnalyzer.Tests;

public class ZetasizerXlsxReaderTests
{
    [Fact]
    public void Read_ThreeRunNumberDistribution_ReturnsSingleDatasetWithThreeRuns()
    {
        using var temp = new TempXlsxFile();
        ZetasizerXlsxFixtures.WriteThreeRunNumberDistribution(temp.Path, "1-41_2_20");

        var datasets = new ZetasizerXlsxReader().Read(temp.Path);

        var dataset = Assert.Single(datasets);
        Assert.Equal("1-41_2_20", dataset.SheetName);
        Assert.Equal("1-41_2_20", dataset.SampleLabel);

        Assert.NotNull(dataset.NumberDistribution);
        Assert.Equal(3, dataset.NumberDistribution!.RunCount);
        Assert.Equal(ZetasizerXlsxFixtures.DefaultSizeBins.Length, dataset.NumberDistribution.SizeBinsNm.Count);
        Assert.Equal(ZetasizerXlsxFixtures.DefaultSizeBins, dataset.NumberDistribution.SizeBinsNm);
        Assert.Equal(0, dataset.NumberDistribution.ActiveRunIndex);

        Assert.Null(dataset.IntensityDistribution);
        Assert.Null(dataset.VolumeDistribution);
        Assert.Null(dataset.Correlation);
        Assert.False(dataset.Metadata.TemperatureCelsius.HasValue);
    }

    [Fact]
    public void Read_FullExportWorkbook_SeparatesNumberIntensityAndCorrelationBlocks()
    {
        using var temp = new TempXlsxFile();
        ZetasizerXlsxFixtures.WriteFullExport(temp.Path, "1-100_3_60");

        var datasets = new ZetasizerXlsxReader().Read(temp.Path);

        var dataset = Assert.Single(datasets);
        Assert.NotNull(dataset.NumberDistribution);
        Assert.NotNull(dataset.IntensityDistribution);
        Assert.Null(dataset.VolumeDistribution);
        Assert.NotNull(dataset.Correlation);

        Assert.Single(dataset.NumberDistribution!.Runs);
        Assert.Single(dataset.IntensityDistribution!.Runs);
        Assert.Single(dataset.Correlation!.Runs);

        Assert.Equal(ZetasizerXlsxFixtures.DefaultSizeBins, dataset.NumberDistribution.SizeBinsNm);
        Assert.Equal(ZetasizerXlsxFixtures.DefaultSizeBins, dataset.IntensityDistribution.SizeBinsNm);
        Assert.Equal(5, dataset.Correlation.TimesMicroseconds.Count);
        Assert.Equal(0.0875, dataset.Correlation.TimesMicroseconds[0]);
    }

    [Fact]
    public void Read_MultipleSheets_ReturnsOneDatasetPerSheetInOrder()
    {
        using var temp = new TempXlsxFile();
        var names = new[] { "1-41_2_20", "1-41_2_30", "1-41_3_40" };
        ZetasizerXlsxFixtures.WriteMultipleSheets(temp.Path, names);

        var datasets = new ZetasizerXlsxReader().Read(temp.Path);

        Assert.Equal(names.Length, datasets.Count);
        Assert.Equal(names, datasets.Select(d => d.SheetName));
        Assert.All(datasets, d => Assert.Equal(3, d.NumberDistribution!.RunCount));
    }

    [Fact]
    public void Read_EmptyWorkbook_ReturnsEmptyList()
    {
        using var temp = new TempXlsxFile();
        ZetasizerXlsxFixtures.WriteEmptyWorkbook(temp.Path);

        var datasets = new ZetasizerXlsxReader().Read(temp.Path);

        Assert.Empty(datasets);
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        var reader = new ZetasizerXlsxReader();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");

        Assert.Throws<FileNotFoundException>(() => reader.Read(path));
    }

    [Fact]
    public void Read_BlankPath_ThrowsArgumentException()
    {
        var reader = new ZetasizerXlsxReader();

        Assert.Throws<ArgumentException>(() => reader.Read(""));
    }

    [Fact]
    public void ParticleSizeDistribution_ActiveRunIndexOutOfRange_ClampsToValidRun()
    {
        var distribution = new ParticleSizeDistribution
        {
            SizeBinsNm = new[] { 1.0, 2.0, 3.0 },
            Runs = new IReadOnlyList<double>[]
            {
                new[] { 0.1, 0.2, 0.3 },
                new[] { 0.4, 0.5, 0.6 },
            },
            ActiveRunIndex = 99,
        };

        Assert.Equal(new[] { 0.4, 0.5, 0.6 }, distribution.ActiveRun);
    }

    [Fact]
    public void ParticleSizeDistribution_NoRuns_ActiveRunIsEmpty()
    {
        var distribution = new ParticleSizeDistribution
        {
            SizeBinsNm = new[] { 1.0, 2.0 },
            Runs = Array.Empty<IReadOnlyList<double>>(),
            ActiveRunIndex = 0,
        };

        Assert.Empty(distribution.ActiveRun);
    }
}
