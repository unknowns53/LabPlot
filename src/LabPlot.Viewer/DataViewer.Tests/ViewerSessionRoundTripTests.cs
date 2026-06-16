using DataViewer.Core;
using LabPlot.Core;

namespace DataViewer.Tests;

public sealed class ViewerSessionRoundTripTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("viewer-session-tests").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SaveLoad_RoundTripsDatasetsSeriesAndAxes()
    {
        var session = new ViewerAnalysisSession
        {
            Overlay = true,
            ActiveDatasetIndex = 0,
            Datasets =
            {
                new ViewerSessionDataset
                {
                    SourceFilePath = @"D:\data\run1.csv",
                    XColumnIndex = 0,
                    Series =
                    {
                        new ViewerSessionSeries
                        {
                            ColumnIndex = 1,
                            ColumnName = "Transmittance",
                            AxisSide = "Right",
                            ChartType = ViewerChartType.LineMarkers.ToToken(),
                            Normalize = true,
                            YOffset = 0.5,
                            SmoothingWindow = 5,
                            Style = new AnalysisSessionStyle { ColorHex = "#2563EB", LegendName = "run 1" },
                        },
                    },
                },
            },
            Axes = new ViewerSessionAxes
            {
                XMin = 1,
                XMax = 100,
                XLogScale = true,
                Y2LogScale = true,
                Y2Min = 0.1,
                Y2Max = 10,
                Y2Label = "Intensity",
            },
            Formatting = new GraphFormattingConfig { FontSize = 18 },
        };

        var path = Path.Combine(_tempDir, "session.gvjson");
        var store = new AnalysisSessionStore<ViewerAnalysisSession>();
        store.Save(session, path);
        var loaded = store.Load(path);

        var dataset = Assert.Single(loaded.Datasets);
        Assert.Equal(@"D:\data\run1.csv", dataset.SourceFilePath);
        var series = Assert.Single(dataset.Series);
        Assert.Equal("Transmittance", series.ColumnName);
        Assert.Equal("Right", series.AxisSide);
        Assert.Equal(ViewerChartType.LineMarkers, ViewerChartTypes.Parse(series.ChartType));
        Assert.True(series.Normalize);
        Assert.Equal(5, series.SmoothingWindow);
        Assert.Equal("#2563EB", series.Style.ColorHex);
        Assert.True(loaded.Axes.XLogScale);
        Assert.Equal(0.1, loaded.Axes.Y2Min);
        Assert.Equal("Intensity", loaded.Axes.Y2Label);
        Assert.Equal(18, loaded.Formatting!.FontSize);
    }

    [Fact]
    public void SaveLoad_EmbeddedClipboardTable_RoundTripsNaNCellsAsNull()
    {
        var table = ClipboardTableParser.Parse("X\tY\n1\t10\n2\t\n3\t30\n");
        var session = new ViewerAnalysisSession
        {
            Datasets =
            {
                new ViewerSessionDataset
                {
                    XColumnIndex = 0,
                    EmbeddedTable = ViewerEmbeddedTable.FromTable(table),
                },
            },
        };

        var path = Path.Combine(_tempDir, "clipboard.gvjson");
        var store = new AnalysisSessionStore<ViewerAnalysisSession>();
        store.Save(session, path);
        var loaded = store.Load(path);

        var restored = loaded.Datasets[0].EmbeddedTable!.ToTable();
        Assert.Equal(new[] { "X", "Y" }, restored.Columns.Select(static column => column.Name));
        Assert.Equal(3, restored.RowCount);
        Assert.Equal(10.0, restored.Columns[1].Values[0]);
        Assert.True(double.IsNaN(restored.Columns[1].Values[1]));
        Assert.Equal(30.0, restored.Columns[1].Values[2]);
        Assert.True(restored.Columns[1].IsNumeric);
    }

    [Fact]
    public void Load_PartialJson_EnsureDefaultsRebuildsContainers()
    {
        var path = Path.Combine(_tempDir, "partial.gvjson");
        File.WriteAllText(path, """{ "Version": 1 }""");

        var loaded = new AnalysisSessionStore<ViewerAnalysisSession>().Load(path);

        Assert.NotNull(loaded.Datasets);
        Assert.NotNull(loaded.Axes);
        Assert.NotNull(loaded.Labels);
    }
}
