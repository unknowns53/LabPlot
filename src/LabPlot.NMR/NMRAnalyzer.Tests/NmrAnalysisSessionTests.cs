using LabPlot.Core;
using NMRAnalyzer.Core;

namespace NMRAnalyzer.Tests;

public class NmrAnalysisSessionTests
{
    [Fact]
    public void SaveLoad_RoundTripsDatasetsRegionsAndShift()
    {
        var session = new NmrAnalysisSession
        {
            Overlay = true,
            ActiveDatasetIndex = 1,
            ReferenceShiftPpm = -0.05,
            Datasets =
            {
                new AnalysisSessionDataset { SourceFilePath = @"C:\data\a.jdf" },
                new AnalysisSessionDataset { SourceFilePath = @"C:\data\b.jdf" },
            },
            IntegrationRegions =
            {
                new NmrIntegrationRegion { Label = "CH3", PpmMin = 0.8, PpmMax = 1.3 },
                new NmrIntegrationRegion { Label = "aromatic", PpmMin = 7.0, PpmMax = 7.4, Baseline = NmrBaselineMode.None },
            },
        };

        var store = new AnalysisSessionStore<NmrAnalysisSession>();
        var path = Path.Combine(Path.GetTempPath(), $"nmr-session-{Guid.NewGuid():N}.json");
        try
        {
            store.Save(session, path);
            var loaded = store.Load(path);

            Assert.True(loaded.Overlay);
            Assert.Equal(1, loaded.ActiveDatasetIndex);
            Assert.Equal(-0.05, loaded.ReferenceShiftPpm, precision: 6);
            Assert.Equal(2, loaded.Datasets.Count);
            Assert.Equal(@"C:\data\b.jdf", loaded.Datasets[1].SourceFilePath);
            Assert.Equal(2, loaded.IntegrationRegions.Count);
            Assert.Equal("CH3", loaded.IntegrationRegions[0].Label);
            Assert.Equal(NmrBaselineMode.None, loaded.IntegrationRegions[1].Baseline);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
