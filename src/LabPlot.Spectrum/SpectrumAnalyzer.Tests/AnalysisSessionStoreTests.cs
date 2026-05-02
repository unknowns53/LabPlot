using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class AnalysisSessionStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.spjson");
        try
        {
            var store = new AnalysisSessionStore();
            var original = CreateSampleSession();
            store.Save(original, path);

            var loaded = store.Load(path);

            Assert.Equal(AnalysisSession.CurrentVersion, loaded.Version);
            Assert.Equal(original.Overlay, loaded.Overlay);
            Assert.Equal(original.ActiveDatasetIndex, loaded.ActiveDatasetIndex);
            Assert.Equal(2, loaded.Datasets.Count);
            Assert.Equal(@"C:\data\sample1.txt", loaded.Datasets[0].SourceFilePath);
            Assert.Equal("#2563EB", loaded.Datasets[0].Style.ColorHex);
            Assert.Equal("HO-Ph-acetylene", loaded.Datasets[0].Style.LegendName);
            Assert.Equal(1.5, loaded.Datasets[0].Style.LineWidth);

            Assert.Equal(200, loaded.Axes.XMin);
            Assert.Equal(800, loaded.Axes.XMax);
            Assert.Equal(0, loaded.Axes.YMin);
            Assert.Equal(1.5, loaded.Axes.YMax);

            Assert.Equal("UV-Vis spectrum", loaded.Labels.Title);
            Assert.Equal("Wavelength / nm", loaded.Labels.XLabel);
            Assert.Equal("Absorbance", loaded.Labels.YLabel);

            Assert.NotNull(loaded.Formatting);
            Assert.Equal("Arial", loaded.Formatting!.FontName);
            Assert.Equal(14, loaded.Formatting.FontSize);
            Assert.False(loaded.Formatting.ShowGrid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RejectsNewerVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.spjson");
        try
        {
            File.WriteAllText(
                path,
                """{ "Version": 999, "Datasets": [], "Axes": {}, "Labels": {} }""");

            Assert.Throws<InvalidDataException>(() => new AnalysisSessionStore().Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_HandlesMissingOptionalSections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.spjson");
        try
        {
            File.WriteAllText(path, """{ "Version": 1 }""");

            var loaded = new AnalysisSessionStore().Load(path);

            Assert.Empty(loaded.Datasets);
            Assert.NotNull(loaded.Axes);
            Assert.NotNull(loaded.Labels);
            Assert.Null(loaded.Formatting);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AnalysisSession CreateSampleSession()
    {
        return new AnalysisSession
        {
            Overlay = true,
            ActiveDatasetIndex = 1,
            Datasets =
            {
                new AnalysisSessionDataset
                {
                    SourceFilePath = @"C:\data\sample1.txt",
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = "#2563EB",
                        LegendName = "HO-Ph-acetylene",
                        LineWidth = 1.5,
                        MarkerSize = 0,
                    },
                },
                new AnalysisSessionDataset
                {
                    SourceFilePath = @"C:\data\sample2.txt",
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = "#DC2626",
                        LegendName = "Reference",
                        LineWidth = 2.0,
                        MarkerSize = 4,
                    },
                },
            },
            Axes = new AnalysisSessionAxes
            {
                XMin = 200,
                XMax = 800,
                YMin = 0,
                YMax = 1.5,
            },
            Labels = new AnalysisSessionLabels
            {
                Title = "UV-Vis spectrum",
                XLabel = "Wavelength / nm",
                YLabel = "Absorbance",
            },
            Formatting = new GraphFormattingConfig
            {
                FontName = "Arial",
                FontSize = 14,
                ShowGrid = false,
                ShowYAxisTickLabels = true,
                ShowPlotFrame = true,
                PlotFrameWidth = 1.2,
                PlotFrameColorHex = "#475569",
                AspectRatio = "16:9",
                LineWidth = 1.5,
                MarkerSize = 0,
            },
        };
    }
}
