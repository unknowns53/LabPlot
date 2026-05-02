using DlsAnalyzer.Core;
using LabPlot.Core;

namespace DlsAnalyzer.Tests;

public sealed class DlsAnalysisSessionTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var session = new DlsAnalysisSession
        {
            WorkbookPath = @"C:\data\sample.xlsx",
            SelectedDistributionMode = "Intensity",
            SelectedRunIndex = 2,
            ActiveDatasetIndex = 1,
            Overlay = true,
            Labels = new AnalysisSessionLabels
            {
                Title = "PNIPAM 25°C",
                XLabel = "Diameter (nm)",
                YLabel = "Intensity (%)",
            },
            Axes = new AnalysisSessionAxes
            {
                XMin = 1.0,
                XMax = 1000.0,
                YMin = 0.0,
                YMax = 30.0,
            },
            Datasets = new List<DlsAnalysisSessionDataset>
            {
                new()
                {
                    SheetName = "1-41_2_20",
                    SourceFilePath = @"C:\data\sample.xlsx",
                    Selected = true,
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = "#DC2626",
                        LegendName = "Sheet 1",
                        LineWidth = 2.5,
                        MarkerSize = 4.0,
                    },
                    Metadata = new DlsAnalysisSessionMetadata
                    {
                        TemperatureCelsius = 25.0,
                        Solvent = "Water",
                        ConcentrationMgPerMl = 1.0,
                        RefractiveIndex = 1.331,
                        ViscosityMpas = 0.89,
                        WavelengthNm = 633.0,
                        ScatteringAngleDegrees = 173.0,
                    },
                    CumulantSettings = new DlsAnalysisSessionCumulantSettings
                    {
                        FitRangeMinMicroseconds = 1.0,
                        FitRangeMaxMicroseconds = 200.0,
                    },
                },
                new()
                {
                    SheetName = "1-41_2_30",
                    SourceFilePath = @"C:\data\sample.xlsx",
                    Selected = false,
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = null, // Auto palette
                        LegendName = null,
                        LineWidth = 1.5,
                        MarkerSize = 0.0,
                    },
                    Metadata = new DlsAnalysisSessionMetadata
                    {
                        // Partial metadata — common in real sessions.
                        TemperatureCelsius = 30.0,
                        Solvent = null,
                        ConcentrationMgPerMl = null,
                        RefractiveIndex = null,
                        ViscosityMpas = null,
                        WavelengthNm = 633.0,
                        ScatteringAngleDegrees = 173.0,
                    },
                    CumulantSettings = new DlsAnalysisSessionCumulantSettings
                    {
                        FitRangeMinMicroseconds = null,
                        FitRangeMaxMicroseconds = null,
                    },
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dlsjson");
        try
        {
            new AnalysisSessionStore<DlsAnalysisSession>().Save(session, path);
            var loaded = new AnalysisSessionStore<DlsAnalysisSession>().Load(path);

            Assert.Equal(session.WorkbookPath, loaded.WorkbookPath);
            Assert.Equal(session.SelectedDistributionMode, loaded.SelectedDistributionMode);
            Assert.Equal(session.SelectedRunIndex, loaded.SelectedRunIndex);
            Assert.Equal(session.ActiveDatasetIndex, loaded.ActiveDatasetIndex);
            Assert.Equal(session.Overlay, loaded.Overlay);
            Assert.Equal(session.Labels.Title, loaded.Labels.Title);
            Assert.Equal(session.Labels.XLabel, loaded.Labels.XLabel);
            Assert.Equal(session.Labels.YLabel, loaded.Labels.YLabel);
            Assert.Equal(session.Axes.XMin, loaded.Axes.XMin);
            Assert.Equal(session.Axes.XMax, loaded.Axes.XMax);

            Assert.Equal(2, loaded.Datasets.Count);

            var first = loaded.Datasets[0];
            Assert.Equal("1-41_2_20", first.SheetName);
            Assert.True(first.Selected);
            Assert.Equal("#DC2626", first.Style.ColorHex);
            Assert.Equal("Sheet 1", first.Style.LegendName);
            Assert.Equal(2.5, first.Style.LineWidth);
            Assert.Equal(25.0, first.Metadata.TemperatureCelsius);
            Assert.Equal("Water", first.Metadata.Solvent);
            Assert.Equal(0.89, first.Metadata.ViscosityMpas);
            Assert.Equal(633.0, first.Metadata.WavelengthNm);
            Assert.Equal(173.0, first.Metadata.ScatteringAngleDegrees);
            Assert.Equal(1.0, first.CumulantSettings.FitRangeMinMicroseconds);
            Assert.Equal(200.0, first.CumulantSettings.FitRangeMaxMicroseconds);

            var second = loaded.Datasets[1];
            Assert.Equal("1-41_2_30", second.SheetName);
            Assert.False(second.Selected);
            Assert.Null(second.Style.ColorHex);
            Assert.Null(second.Style.LegendName);
            Assert.Null(second.Metadata.RefractiveIndex);
            Assert.Null(second.CumulantSettings.FitRangeMinMicroseconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureDefaults_RecreatesNullContainers()
    {
        // Simulate a sparse JSON payload where collections came back null.
        var session = new DlsAnalysisSession
        {
            Datasets = null!,
            Axes = null!,
            Labels = null!,
        };

        session.EnsureDefaults();

        Assert.NotNull(session.Datasets);
        Assert.Empty(session.Datasets);
        Assert.NotNull(session.Axes);
        Assert.NotNull(session.Labels);
    }

    [Fact]
    public void EnsureDefaults_FillsPerDatasetSubObjects()
    {
        var session = new DlsAnalysisSession
        {
            Datasets = new List<DlsAnalysisSessionDataset>
            {
                new()
                {
                    SheetName = "Sheet1",
                    Style = null!,
                    Metadata = null!,
                    CumulantSettings = null!,
                },
            },
        };

        session.EnsureDefaults();

        var entry = session.Datasets[0];
        Assert.NotNull(entry.Style);
        Assert.NotNull(entry.Metadata);
        Assert.NotNull(entry.CumulantSettings);
    }

    [Fact]
    public void Save_StampsVersionAndSavedAt()
    {
        var session = new DlsAnalysisSession();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dlsjson");
        try
        {
            var beforeSave = DateTimeOffset.Now;
            new AnalysisSessionStore<DlsAnalysisSession>().Save(session, path);

            var loaded = new AnalysisSessionStore<DlsAnalysisSession>().Load(path);
            Assert.Equal(AnalysisSession.CurrentVersion, loaded.Version);
            Assert.True(loaded.SavedAt >= beforeSave.AddSeconds(-1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
