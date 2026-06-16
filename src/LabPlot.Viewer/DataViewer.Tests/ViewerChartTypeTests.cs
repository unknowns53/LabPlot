using DataViewer.Core;

namespace DataViewer.Tests;

public sealed class ViewerChartTypeTests
{
    [Theory]
    [InlineData("Line", ViewerChartType.Line)]
    [InlineData("markers", ViewerChartType.Markers)]
    [InlineData("LineMarkers", ViewerChartType.LineMarkers)]
    [InlineData("BAR", ViewerChartType.Bar)]
    public void Parse_KnownTokens_RoundTripsCaseInsensitively(string token, ViewerChartType expected)
    {
        Assert.Equal(expected, ViewerChartTypes.Parse(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pie")]
    [InlineData("42")]
    public void Parse_MissingOrUnknownToken_FallsBackToLine(string? token)
    {
        Assert.Equal(ViewerChartType.Line, ViewerChartTypes.Parse(token));
    }

    [Fact]
    public void ToToken_RoundTripsThroughParse()
    {
        foreach (var type in Enum.GetValues<ViewerChartType>())
        {
            Assert.Equal(type, ViewerChartTypes.Parse(type.ToToken()));
        }
    }

    [Theory]
    [InlineData(ViewerChartType.Line, true, false)]
    [InlineData(ViewerChartType.Markers, false, true)]
    [InlineData(ViewerChartType.LineMarkers, true, true)]
    [InlineData(ViewerChartType.Bar, false, false)]
    public void ShowsLineAndMarkers_MatchType(ViewerChartType type, bool line, bool markers)
    {
        Assert.Equal(line, type.ShowsLine());
        Assert.Equal(markers, type.ShowsMarkers());
    }
}
