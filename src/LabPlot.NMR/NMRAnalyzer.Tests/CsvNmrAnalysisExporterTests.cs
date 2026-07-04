using LabPlot.Core;
using NMRAnalyzer.Core;

namespace NMRAnalyzer.Tests;

public class CsvNmrAnalysisExporterTests
{
    [Fact]
    public void ToText_WritesPpmIntensitySection()
    {
        var export = new AnalysisExport
        {
            GeneratorName = "NMR Analyzer",
            Entries = new[]
            {
                new NmrAnalysisExportEntry
                {
                    DisplayName = "sample",
                    XLabel = "ppm",
                    YLabel = "Intensity",
                    Points = new[]
                    {
                        new NmrDataPoint(7.26, 100.0),
                        new NmrDataPoint(0.0, 5.0),
                    },
                },
            },
        };

        var text = new CsvNmrAnalysisExporter().ToText(export);

        Assert.Contains("# NMR Analyzer analysis export", text);
        Assert.Contains("# Spectrum (sample)", text);
        Assert.Contains("ppm,Intensity", text);
        Assert.Contains("7.26,100", text);
    }

    [Fact]
    public void IntegrationTableToText_WritesRegionRows()
    {
        var results = new[]
        {
            new NmrIntegrationResult
            {
                Region = new NmrIntegrationRegion { Label = "CH3", PpmMin = 0.8, PpmMax = 1.3 },
                Area = 3.0, RawArea = 3.0, BaselineArea = 0.0, PointCount = 10, Ratio = 3.0,
            },
            new NmrIntegrationResult
            {
                Region = new NmrIntegrationRegion { Label = "CH", PpmMin = 3.4, PpmMax = 3.6 },
                Area = 1.0, RawArea = 1.0, BaselineArea = 0.0, PointCount = 6, Ratio = 1.0,
            },
        };

        var text = CsvNmrAnalysisExporter.IntegrationTableToText(results);

        Assert.Contains("Region,PpmMin,PpmMax,Area,Ratio,PointCount", text);
        Assert.Contains("CH3,0.8,1.3,3,3,10", text);
        Assert.Contains("CH,3.4,3.6,1,1,6", text);
    }
}
