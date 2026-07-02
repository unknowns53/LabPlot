using GpcAnalyzer.Core;

namespace GpcAnalyzer.Tests;

public sealed class GpcResultFormatTests
{
    [Theory]
    [InlineData(4049.0, "4,049")]
    [InlineData(12740.3, "12,740")]
    [InlineData(0.0, "0")]
    public void FormatMolecularWeight_UsesGroupedInteger(double value, string expected)
    {
        Assert.Equal(expected, GpcResultFormat.FormatMolecularWeight(value));
    }

    [Fact]
    public void FormatMolecularWeight_ReturnsDashForNullOrNonFinite()
    {
        Assert.Equal("-", GpcResultFormat.FormatMolecularWeight((double?)null));
        Assert.Equal("-", GpcResultFormat.FormatMolecularWeight(double.NaN));
        Assert.Equal("-", GpcResultFormat.FormatMolecularWeight(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(3.147, "3.147")]
    [InlineData(0.001, "1E-3")]
    [InlineData(0.0, "0")]
    public void FormatRatio_KeepsDecimalNotation(double value, string expected)
    {
        Assert.Equal(expected, GpcResultFormat.FormatRatio(value));
    }

    [Fact]
    public void FormatRatio_ReturnsDashForNullOrNonFinite()
    {
        Assert.Equal("-", GpcResultFormat.FormatRatio((double?)null));
        Assert.Equal("-", GpcResultFormat.FormatRatio(double.NaN));
    }
}
