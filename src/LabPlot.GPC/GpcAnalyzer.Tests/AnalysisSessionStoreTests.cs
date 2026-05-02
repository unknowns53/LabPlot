using GpcAnalyzer.Core;
using LabPlot.Core;

namespace GpcAnalyzer.Tests;

public sealed class AnalysisSessionStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.gpcjson");
        try
        {
            var store = new AnalysisSessionStore<GpcAnalysisSession>();
            var original = CreateSampleSession();
            store.Save(original, path);

            var loaded = store.Load(path);

            Assert.Equal(AnalysisSession.CurrentVersion, loaded.Version);
            Assert.Equal(original.Overlay, loaded.Overlay);
            Assert.Equal(original.ActiveDatasetIndex, loaded.ActiveDatasetIndex);
            Assert.Equal(2, loaded.Datasets.Count);
            Assert.Equal(@"C:\data\sample1.txt", loaded.Datasets[0].SourceFilePath);
            Assert.Equal("A", loaded.Datasets[0].Detector);
            Assert.Equal("2", loaded.Datasets[0].SelectedPeakId);
            Assert.Equal("#2563EB", loaded.Datasets[0].Style.ColorHex);
            Assert.Equal("PNIPAM", loaded.Datasets[0].Style.LegendName);
            Assert.Equal(1.5, loaded.Datasets[0].Style.LineWidth);

            Assert.NotNull(loaded.Calibration);
            Assert.Equal(@"C:\curves\standard.json", loaded.Calibration!.FilePath);
            Assert.Equal("DMF", loaded.Calibration.Solvent);
            Assert.Equal("RI", loaded.Calibration.Detector);

            Assert.True(loaded.MolecularWeight.Enabled);
            Assert.Equal(nameof(MolecularWeightYMode.DifferentialWeightFraction), loaded.MolecularWeight.YMode);
            Assert.Equal(100.0, loaded.MolecularWeight.MinMolecularWeight);
            Assert.Equal(10_000_000.0, loaded.MolecularWeight.MaxMolecularWeight);

            Assert.Equal(nameof(AnalysisSessionAxisMode.MolecularWeight), loaded.Axes.Mode);
            Assert.Equal(1000, loaded.Axes.XMin);
            Assert.Equal(1_000_000, loaded.Axes.XMax);
            Assert.Equal(0, loaded.Axes.YMin);
            Assert.Equal(1.5, loaded.Axes.YMax);

            Assert.Equal("My GPC", loaded.Labels.Title);
            Assert.Equal("Molecular Weight", loaded.Labels.XLabel);
            Assert.Equal("dw/dlogM", loaded.Labels.YLabel);

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
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.gpcjson");
        try
        {
            var session = new GpcAnalysisSession { Version = AnalysisSession.CurrentVersion + 5 };
            new AnalysisSessionStore<GpcAnalysisSession>().Save(session, path);

            // Save() resets Version to CurrentVersion, so we have to write a manual file
            // that contains the future version directly.
            File.WriteAllText(
                path,
                """{ "Version": 999, "Datasets": [], "MolecularWeight": {}, "Axes": {}, "Labels": {} }""");

            Assert.Throws<InvalidDataException>(() => new AnalysisSessionStore<GpcAnalysisSession>().Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_HandlesMissingOptionalSections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.gpcjson");
        try
        {
            File.WriteAllText(path, """{ "Version": 1 }""");

            var loaded = new AnalysisSessionStore<GpcAnalysisSession>().Load(path);

            Assert.Empty(loaded.Datasets);
            Assert.NotNull(loaded.MolecularWeight);
            Assert.NotNull(loaded.Axes);
            Assert.NotNull(loaded.Labels);
            Assert.Null(loaded.Formatting);
            Assert.Null(loaded.Calibration);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static GpcAnalysisSession CreateSampleSession()
    {
        return new GpcAnalysisSession
        {
            Overlay = true,
            ActiveDatasetIndex = 1,
            Datasets =
            {
                new GpcAnalysisSessionDataset
                {
                    SourceFilePath = @"C:\data\sample1.txt",
                    Detector = "A",
                    SelectedPeakId = "2",
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = "#2563EB",
                        LegendName = "PNIPAM",
                        LineWidth = 1.5,
                        MarkerSize = 0,
                    },
                },
                new GpcAnalysisSessionDataset
                {
                    SourceFilePath = @"C:\data\sample2.txt",
                    Detector = "B",
                    SelectedPeakId = null,
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = "#DC2626",
                        LegendName = "PMMA",
                        LineWidth = 2.0,
                        MarkerSize = 4,
                    },
                },
            },
            Calibration = new AnalysisSessionCalibration
            {
                FilePath = @"C:\curves\standard.json",
                Solvent = "DMF",
                Detector = "RI",
            },
            MolecularWeight = new AnalysisSessionMolecularWeight
            {
                Enabled = true,
                YMode = nameof(MolecularWeightYMode.DifferentialWeightFraction),
                MinMolecularWeight = 100,
                MaxMolecularWeight = 10_000_000,
            },
            Axes = new GpcAnalysisSessionAxes
            {
                Mode = nameof(AnalysisSessionAxisMode.MolecularWeight),
                XMin = 1000,
                XMax = 1_000_000,
                YMin = 0,
                YMax = 1.5,
            },
            Labels = new AnalysisSessionLabels
            {
                Title = "My GPC",
                XLabel = "Molecular Weight",
                YLabel = "dw/dlogM",
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
