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
    public void DemoWorkbookContainsScenarioAndRampSheets()
    {
        var reader = new ZetasizerXlsxReader();
        var datasets = reader.Read(DemoXlsxPath());

        // Two showcase sheets (single coil, bimodal globule), the
        // temperature ramp series (8 stops across the LCST), and the
        // 25 C concentration series (7 stops between 0.5 and 10 mg/mL).
        Assert.Contains(datasets, d => d.SheetName == "PNIPAM_25C");
        Assert.Contains(datasets, d => d.SheetName == "PNIPAM_35C");

        var rampCount = datasets.Count(d => d.SheetName.StartsWith("PNIPAM_ramp_"));
        Assert.True(rampCount >= 6, $"Ramp series should contribute at least 6 sheets but got {rampCount}");

        var concCount = datasets.Count(d => d.SheetName.StartsWith("PNIPAM_conc_"));
        Assert.True(concCount >= 5, $"Concentration series should contribute at least 5 sheets but got {concCount}");
    }

    [Fact]
    public void ConcentrationSeriesSheetsRecoverNegativeKD()
    {
        var reader = new ZetasizerXlsxReader();
        var datasets = reader.Read(DemoXlsxPath());

        // Same recipe the demo generator started from: T = 25 C, water
        // viscosity 0.890 mPa·s, k_D = -25 mL/g, d_h(c=0) = 10 nm. The
        // sheet name carries the concentration in its suffix.
        var concSheets = datasets.Where(d => d.SheetName.StartsWith("PNIPAM_conc_")).ToList();
        var stops = new (double C, string Suffix)[]
        {
            (0.5, "0p5"), (1.0, "1"), (2.0, "2"), (4.0, "4"),
            (6.0, "6"), (8.0, "8"), (10.0, "10"),
        };

        var points = new List<ConcentrationSeriesPoint>();
        foreach (var (c, suffix) in stops)
        {
            var sheet = concSheets.FirstOrDefault(s => s.SheetName.EndsWith("_" + suffix + "mgmL"));
            if (sheet?.Correlation is null) continue;
            var cumulant = CumulantAnalyzer.Analyze(sheet.Correlation);
            if (!cumulant.Success) continue;
            var size = StokesEinstein.Compute(
                cumulant.Result!.FirstCumulantPerMicrosecond,
                temperatureCelsius: 25.0,
                viscosityMpas: 0.890,
                refractiveIndex: 1.330,
                wavelengthNm: 633.0,
                scatteringAngleDegrees: 173.0);
            if (!size.Success || size.DiffusionCoefficientM2PerSecond is null) continue;
            points.Add(new ConcentrationSeriesPoint(c, size.DiffusionCoefficientM2PerSecond.Value));
        }

        var outcome = ConcentrationSeriesAnalyzer.Analyze(points, 25.0, 0.890);
        Assert.True(outcome.Success, outcome.FailureReason);
        // Expect d_h(c=0) within ±15% of 10 nm and k_D negative with
        // magnitude in the right ballpark; cumulant noise + finite
        // sampling allow loose bounds compared with the synthetic core
        // tests.
        Assert.InRange(outcome.Result!.HydrodynamicDiameterAtZeroConcentrationNm, 8.5, 11.5);
        Assert.True(outcome.Result.KDmlPerGram < 0,
            $"Expected attractive interaction (k_D < 0) but got {outcome.Result.KDmlPerGram} mL/g");
        Assert.InRange(outcome.Result.KDmlPerGram, -45.0, -10.0);
    }

    [Fact]
    public void TemperatureRampSheetsRecoverBoltzmannParameters()
    {
        var reader = new ZetasizerXlsxReader();
        var datasets = reader.Read(DemoXlsxPath());

        // For the ramp series we know each sheet's intended (T, d_h)
        // pair from the generator: T is encoded in the sheet name and
        // d_h follows the Boltzmann recipe T_c = 31, w = 0.8, plateaus
        // 10 / 200 nm. Recover (T, d_h) from each ramp sheet by running
        // the exact pipeline the UI runs (cumulant fit + Stokes-Einstein
        // with the per-sheet solvent/optics) and verify the ramp fit
        // returns LCST ≈ 31 °C.
        var rampSheets = datasets.Where(d => d.SheetName.StartsWith("PNIPAM_ramp_")).ToList();
        var stops = new (double T, double Eta, string Suffix)[]
        {
            (25.0, 0.890, "25C"), (27.0, 0.852, "27C"), (29.0, 0.818, "29C"),
            (30.0, 0.798, "30C"), (31.0, 0.781, "31C"), (32.0, 0.765, "32C"),
            (33.0, 0.748, "33C"), (35.0, 0.719, "35C"),
        };

        var points = new List<TemperatureRampPoint>();
        foreach (var (t, eta, suffix) in stops)
        {
            var sheet = rampSheets.FirstOrDefault(s => s.SheetName.EndsWith("_" + suffix));
            if (sheet?.Correlation is null) continue;
            var cumulant = CumulantAnalyzer.Analyze(sheet.Correlation);
            if (!cumulant.Success) continue;
            var size = StokesEinstein.Compute(
                cumulant.Result!.FirstCumulantPerMicrosecond,
                temperatureCelsius: t,
                viscosityMpas: eta,
                refractiveIndex: 1.330,
                wavelengthNm: 633.0,
                scatteringAngleDegrees: 173.0);
            if (!size.Success) continue;
            points.Add(new TemperatureRampPoint(t, size.HydrodynamicDiameterNm!.Value));
        }

        var rampOutcome = TemperatureRampAnalyzer.Analyze(points);
        Assert.True(rampOutcome.Success, rampOutcome.FailureReason);
        Assert.InRange(rampOutcome.Result!.TransitionTemperatureCelsius, 30.0, 32.0);
        Assert.InRange(Math.Abs(rampOutcome.Result.TransitionWidthCelsius), 0.4, 1.5);
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
