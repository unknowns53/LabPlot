using DlsAnalyzer.Core;

namespace DlsAnalyzer.Tests;

/// <summary>
/// Round-trip the bundled demo workbook through the same pipeline a
/// real Zetasizer xlsx would take (reader → cumulant fit → Stokes
/// Einstein) and assert the recovered diameter matches the recipe the
/// generator started from. Both protects the demo file against silent
/// regeneration regressions and serves as a worked example of the
/// "raw ACF to particle size" round trip working end to end.
/// </summary>
public sealed class DemoXlsxReadbackTests
{
    /// <summary>Resolve the demo workbook path relative to the repo root.</summary>
    private static string DemoXlsxPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "LabPlot.DLS", "samples", "demo.xlsx");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new FileNotFoundException(
            "demo.xlsx not found above the test binary directory; did the generator run?",
            "src/LabPlot.DLS/samples/demo.xlsx");
    }

    [Fact]
    public void DemoWorkbookContainsBothScenarioSheets()
    {
        var reader = new ZetasizerXlsxReader();
        var datasets = reader.Read(DemoXlsxPath());

        Assert.Collection(datasets,
            ds => Assert.Equal("PNIPAM_25C", ds.SheetName),
            ds => Assert.Equal("PNIPAM_35C", ds.SheetName));
    }

    [Fact]
    public void CoilSheetCarriesDistributionsAndCorrelation()
    {
        var reader = new ZetasizerXlsxReader();
        var datasets = reader.Read(DemoXlsxPath());

        var coil = datasets.Single(d => d.SheetName == "PNIPAM_25C");
        Assert.NotNull(coil.NumberDistribution);
        Assert.NotNull(coil.IntensityDistribution);
        Assert.NotNull(coil.VolumeDistribution);
        Assert.NotNull(coil.Correlation);
        Assert.Equal(3, coil.Correlation!.RunCount);
        Assert.True(coil.Correlation.TimesMicroseconds.Count > 50);
    }

    [Fact]
    public void CoilCumulantFitRecoversTenNanometerDiameter()
    {
        var reader = new ZetasizerXlsxReader();
        var datasets = reader.Read(DemoXlsxPath());
        var coil = datasets.Single(d => d.SheetName == "PNIPAM_25C");

        var outcome = CumulantAnalyzer.Analyze(coil.Correlation);
        Assert.True(outcome.Success, outcome.FailureReason);
        var size = StokesEinstein.Compute(
            outcome.Result!.FirstCumulantPerMicrosecond,
            temperatureCelsius: 25.0,
            viscosityMpas: 0.890,
            refractiveIndex: 1.330,
            wavelengthNm: 633.0,
            scatteringAngleDegrees: 173.0);

        Assert.True(size.Success, "Stokes-Einstein conversion missed metadata");
        var recoveredNm = size.HydrodynamicDiameterNm!.Value;
        // Recipe puts the coil at 10.0 nm. Allow +-15% to absorb the
        // injected gaussian noise plus the fact that the cumulant fit
        // sees a slightly polydisperse trace (PdI = 0.08 in the recipe).
        Assert.InRange(recoveredNm, 8.5, 11.5);
    }
}
