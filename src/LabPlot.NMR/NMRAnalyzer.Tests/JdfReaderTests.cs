using NMRAnalyzer.Core;

namespace NMRAnalyzer.Tests;

public class JdfReaderTests
{
    private static NmrDataset Parse(byte[] jdf) =>
        new JdfReader().Parse(new MemoryStream(jdf), "synthetic.jdf");

    [Fact]
    public void Parse_UsesHeaderDirectValuesForPpmAxis()
    {
        // Regression guard for the ~1.8 ppm error that the guess_udic
        // back-calculation introduces on processed spectra: the axis must
        // come straight from data_axis_start/stop.
        var real = new[] { 1.0, 2.0, 3.0, 4.0 };
        var imag = new[] { 0.0, 0.0, 0.0, 0.0 };
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imag, axisStartPpm: 12.52, axisStopPpm: -2.47);

        var dataset = Parse(jdf);

        Assert.Equal(12.52, dataset.XValues[0], precision: 6);
        Assert.Equal(-2.47, dataset.XValues[^1], precision: 6);
        Assert.Equal(4, dataset.XValues.Length);
    }

    [Fact]
    public void Parse_SplitsRealAndImaginaryAsBlocksNotInterleaved()
    {
        // If the two sections were misread as interleaved, RealValues would
        // pick up imaginary samples (e.g. [1, 10, 2, 20]).
        var real = new[] { 1.0, 2.0, 3.0, 4.0 };
        var imag = new[] { 10.0, 20.0, 30.0, 40.0 };
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imag, 10.0, 0.0);

        var dataset = Parse(jdf);

        Assert.Equal(real, dataset.RealValues);
    }

    [Fact]
    public void Parse_RecoversImaginaryPartWithSignConvention()
    {
        // JEOL stores section1 negated: complex = real - i·section1. The
        // fixture stores -imag, so the reader must return imag unchanged.
        var real = new[] { 1.0, 2.0, 3.0 };
        var imag = new[] { 5.0, -6.0, 7.0 };
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imag, 8.0, 1.0);

        var dataset = Parse(jdf);

        Assert.NotNull(dataset.ImaginaryValues);
        Assert.Equal(imag, dataset.ImaginaryValues!);
    }

    [Fact]
    public void Parse_HonorsBodyEndiannessIndependentOfHeader()
    {
        // Header fields are always big-endian; only the body follows the flag.
        var real = new[] { 1.5, 2.5, 3.5, 4.5 };
        var imag = new[] { 0.0, 0.0, 0.0, 0.0 };
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imag, 9.0, -1.0, bodyLittleEndian: true);

        var dataset = Parse(jdf);

        Assert.Equal(real, dataset.RealValues);
        Assert.Equal(9.0, dataset.XValues[0], precision: 6);
        Assert.Equal(-1.0, dataset.XValues[^1], precision: 6);
    }

    [Fact]
    public void Parse_TrimsToOffsetStartStopInclusive()
    {
        // 6 points, keep indices 2..5 inclusive -> 4 points.
        var real = new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0 };
        var imag = new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imag, 12.0, -3.0, trimStart: 2, trimStop: 5);

        var dataset = Parse(jdf);

        Assert.Equal(new[] { 2.0, 3.0, 4.0, 5.0 }, dataset.RealValues);
        Assert.Equal(4, dataset.XValues.Length);
        // The ppm axis spans the header endpoints across the trimmed length.
        Assert.Equal(12.0, dataset.XValues[0], precision: 6);
        Assert.Equal(-3.0, dataset.XValues[^1], precision: 6);
    }

    [Fact]
    public void Parse_ReadsRealOnlySpectrumWithoutImaginary()
    {
        var real = new[] { 1.0, 2.0, 3.0 };
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imaginary: null, axisStartPpm: 7.0, axisStopPpm: 0.0);

        var dataset = Parse(jdf);

        Assert.Equal(real, dataset.RealValues);
        Assert.Null(dataset.ImaginaryValues);
    }

    [Fact]
    public void Parse_ThrowsForNon1DDataFormat()
    {
        var real = new[] { 1.0, 2.0 };
        var imag = new[] { 0.0, 0.0 };
        // dataFormat 2 = two_d, which this version does not support.
        var jdf = JdfTestFixtures.BuildMinimal1D(real, imag, 5.0, 0.0, dataFormat: 2);

        Assert.Throws<NotSupportedException>(() => Parse(jdf));
    }

    [Fact]
    public void Parse_ThrowsForTruncatedHeader()
    {
        var jdf = new byte[100]; // shorter than the 1360-byte header
        Assert.Throws<InvalidDataException>(() => Parse(jdf));
    }

    [Fact]
    public void Read_ThrowsForMissingFile()
    {
        var reader = new JdfReader();
        Assert.Throws<FileNotFoundException>(() => reader.Read(@"Z:\does\not\exist.jdf"));
    }
}
