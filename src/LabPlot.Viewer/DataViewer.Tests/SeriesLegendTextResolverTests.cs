using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class SeriesLegendTextResolverTests
{
    [Fact]
    public void Resolve_LegendNameSet_IsUsedAndTrimmed()
    {
        var result = SeriesLegendTextResolver.Resolve(
            legendName: "  run 1  ",
            columnName: "Transmittance",
            tableDisplayName: "sample.csv",
            multipleTablesLoaded: false);

        Assert.Equal("run 1", result);
    }

    [Fact]
    public void Resolve_LegendNameWhitespaceOnly_IsTreatedAsUnset()
    {
        var result = SeriesLegendTextResolver.Resolve(
            legendName: "   ",
            columnName: "Transmittance",
            tableDisplayName: "sample.csv",
            multipleTablesLoaded: false);

        Assert.Equal("Transmittance", result);
    }

    [Fact]
    public void Resolve_LegendNameNull_FallsBackToColumnName()
    {
        var result = SeriesLegendTextResolver.Resolve(
            legendName: null,
            columnName: "Transmittance",
            tableDisplayName: "sample.csv",
            multipleTablesLoaded: false);

        Assert.Equal("Transmittance", result);
    }

    [Fact]
    public void Resolve_SingleTableLoaded_UsesColumnNameOnly()
    {
        var result = SeriesLegendTextResolver.Resolve(
            legendName: null,
            columnName: "Y1",
            tableDisplayName: "sample.csv",
            multipleTablesLoaded: false);

        Assert.Equal("Y1", result);
    }

    [Fact]
    public void Resolve_MultipleTablesLoaded_PrefixesWithTableDisplayName()
    {
        var result = SeriesLegendTextResolver.Resolve(
            legendName: null,
            columnName: "Y1",
            tableDisplayName: "sample.csv",
            multipleTablesLoaded: true);

        Assert.Equal("sample.csv: Y1", result);
    }
}
