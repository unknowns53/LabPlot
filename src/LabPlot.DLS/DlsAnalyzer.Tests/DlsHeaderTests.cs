using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

public class DlsHeaderTests
{
    [Theory]
    [InlineData("Size (d.nm) - 1-41_2_20 [Steady state]", DlsColumnKind.SizeAxis, "1-41_2_20", "Steady state")]
    [InlineData("Number (Percent) - 1-41_2_20 [Steady state]", DlsColumnKind.NumberPercent, "1-41_2_20", "Steady state")]
    [InlineData("Intensity (Percent) - 1-100_1_40 [Steady state]", DlsColumnKind.IntensityPercent, "1-100_1_40", "Steady state")]
    [InlineData("Volume (Percent) - sampleA [Steady state]", DlsColumnKind.VolumePercent, "sampleA", "Steady state")]
    [InlineData("Time (µs) - 1-100_1_40 [Steady state]", DlsColumnKind.TimeAxis, "1-100_1_40", "Steady state")]
    [InlineData("Correlation Coefficient (g₂-1) - 1-100_1_40 [Steady state]", DlsColumnKind.CorrelationG2Minus1, "1-100_1_40", "Steady state")]
    public void Parse_KnownHeaders_DetectsKindAndSampleAndState(string raw, DlsColumnKind kind, string sample, string state)
    {
        var header = DlsHeader.Parse(raw);

        Assert.Equal(kind, header.Kind);
        Assert.Equal(sample, header.SampleLabel);
        Assert.Equal(state, header.State);
        Assert.Equal(raw, header.Raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankInput_ReturnsEmpty(string? raw)
    {
        var header = DlsHeader.Parse(raw);

        Assert.Equal(DlsColumnKind.Unknown, header.Kind);
        Assert.Null(header.SampleLabel);
        Assert.Null(header.State);
    }

    [Fact]
    public void Parse_HeaderWithoutBracketState_LeavesStateNull()
    {
        var header = DlsHeader.Parse("Size (d.nm) - sampleA");

        Assert.Equal(DlsColumnKind.SizeAxis, header.Kind);
        Assert.Equal("sampleA", header.SampleLabel);
        Assert.Null(header.State);
    }

    [Fact]
    public void Parse_UnrecognisedDataType_ReturnsUnknown()
    {
        var header = DlsHeader.Parse("Some random column [Steady state]");

        Assert.Equal(DlsColumnKind.Unknown, header.Kind);
    }

    [Fact]
    public void Parse_HeaderWithoutSeparator_LeavesSampleLabelNull()
    {
        var header = DlsHeader.Parse("Size (d.nm)");

        Assert.Equal(DlsColumnKind.SizeAxis, header.Kind);
        Assert.Null(header.SampleLabel);
    }
}
