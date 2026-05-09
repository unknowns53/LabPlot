using ClosedXML.Excel;

namespace LabPlot.Tools.DlsSampleGenerator;

/// <summary>
/// One scattering population that contributes to a synthetic Zetasizer
/// trace. <see cref="IntensityWeight"/> mixes populations in g1(tau)
/// according to their light-scattering-weighted contribution; weights
/// are normalised inside the builder.
/// </summary>
internal sealed record DlsPopulation(double DiameterNm, double IntensityWeight, double PolydispersityIndex);

/// <summary>
/// Recipe describing one synthetic worksheet. Carries the optical /
/// solvent / temperature configuration plus the scattering populations.
/// </summary>
internal sealed record DlsSyntheticSheet
{
    public required string SheetName { get; init; }
    public required string SampleLabel { get; init; }
    public required double TemperatureCelsius { get; init; }
    public required double ViscosityMpas { get; init; }
    public required double RefractiveIndex { get; init; }
    public required double WavelengthNm { get; init; }
    public required double ScatteringAngleDegrees { get; init; }
    public required IReadOnlyList<DlsPopulation> Populations { get; init; }

    public double Beta { get; init; } = 0.92;
    public double NoiseSigma { get; init; } = 0.003;
    public int RunCount { get; init; } = 3;

    public int SizeBinCount { get; init; } = 70;
    public double SizeMinNm { get; init; } = 0.4;
    public double SizeMaxNm { get; init; } = 10000.0;

    public int CorrelationPointCount { get; init; } = 100;
    public double TimeMinMicroseconds { get; init; } = 0.5;
    public double TimeMaxMicroseconds { get; init; } = 1.0e7;

    public int Seed { get; init; } = 20260508;
}

/// <summary>
/// Builds a Zetasizer-shaped xlsx from <see cref="DlsSyntheticSheet"/>
/// recipes. The resulting workbook is the canonical demo bundled under
/// src/LabPlot.DLS/samples/, and round-trips through
/// ZetasizerXlsxReader → CumulantAnalyzer → StokesEinstein with a
/// recovered diameter that matches the recipe to within a few percent.
/// </summary>
internal static class DlsSampleBuilder
{
    public static DlsSyntheticSheet PnipamCoilAt25C { get; } = new()
    {
        SheetName = "PNIPAM_25C",
        SampleLabel = "PNIPAM coil 25C",
        TemperatureCelsius = 25.0,
        ViscosityMpas = 0.890,
        RefractiveIndex = 1.330,
        WavelengthNm = 633.0,
        ScatteringAngleDegrees = 173.0,
        Populations = new[]
        {
            new DlsPopulation(DiameterNm: 10.0, IntensityWeight: 1.0, PolydispersityIndex: 0.08),
        },
    };

    public static DlsSyntheticSheet PnipamGlobuleAt35C { get; } = new()
    {
        SheetName = "PNIPAM_35C",
        SampleLabel = "PNIPAM globule 35C",
        TemperatureCelsius = 35.0,
        ViscosityMpas = 0.719,
        RefractiveIndex = 1.330,
        WavelengthNm = 633.0,
        ScatteringAngleDegrees = 173.0,
        // Above LCST the chain collapses into ~200 nm globules; a small
        // fraction of free chains remains, producing a clearly bimodal
        // distribution that is the textbook target for CONTIN-style
        // analysis (which LabPlot does not yet implement).
        Populations = new[]
        {
            new DlsPopulation(DiameterNm: 8.0,   IntensityWeight: 0.20, PolydispersityIndex: 0.10),
            new DlsPopulation(DiameterNm: 200.0, IntensityWeight: 0.80, PolydispersityIndex: 0.15),
        },
        Seed = 20260509,
    };

    /// <summary>
    /// Generates a temperature-ramp series across the LCST. Each entry
    /// is a single PNIPAM measurement at one temperature; concatenated
    /// they trace a Boltzmann sigmoid in d_h. Used by both the bundled
    /// demo workbook and any tests that exercise the ramp analyzer.
    /// </summary>
    public static IEnumerable<DlsSyntheticSheet> PnipamTemperatureRamp()
    {
        // Boltzmann recipe: T_c = 31 C, w = 0.8 C, d_low = 10 nm, d_high = 200 nm
        const double tc = 31.0;
        const double w = 0.8;
        const double dLow = 10.0;
        const double dHigh = 200.0;

        var stops = new (double T, double Eta)[]
        {
            (25.0, 0.890),
            (27.0, 0.852),
            (29.0, 0.818),
            (30.0, 0.798),
            (31.0, 0.781),
            (32.0, 0.765),
            (33.0, 0.748),
            (35.0, 0.719),
        };

        int seed = 20260520;
        foreach (var (t, eta) in stops)
        {
            var s = 1.0 / (1.0 + Math.Exp(-(t - tc) / w));
            var dh = dLow + (dHigh - dLow) * s;
            yield return new DlsSyntheticSheet
            {
                SheetName = $"PNIPAM_ramp_{t.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', 'p')}C",
                SampleLabel = $"PNIPAM ramp {t:0.#}C",
                TemperatureCelsius = t,
                ViscosityMpas = eta,
                RefractiveIndex = 1.330,
                WavelengthNm = 633.0,
                ScatteringAngleDegrees = 173.0,
                Populations = new[]
                {
                    new DlsPopulation(DiameterNm: dh, IntensityWeight: 1.0,
                        PolydispersityIndex: t < tc - 1.0 ? 0.08 : (t > tc + 1.0 ? 0.15 : 0.25)),
                },
                Seed = seed++,
            };
        }
    }

    /// <summary>
    /// Generates a concentration-series at 25 C across the dilute regime
    /// (0.5 - 10 mg/mL). Each entry is a single PNIPAM measurement at one
    /// concentration; concatenated they trace D(c) = D0 (1 + k_D c) with
    /// k_D = -25 mL/g (an attractive-interaction PNIPAM-like recipe).
    /// </summary>
    public static IEnumerable<DlsSyntheticSheet> PnipamConcentrationSeries()
    {
        // d_h(c=0) = 10 nm, k_D = -25 mL/g. Apparent diameter follows
        //   d_h(c) = d_h(c=0) / (1 + k_D c [g/mL])
        // because D(c) = D0 (1 + k_D c) and d_h ∝ 1/D.
        const double dHydroAtZero = 10.0;
        const double kD = -25.0;

        var stops = new (double C, string Suffix)[]
        {
            (0.5,  "0p5"),
            (1.0,  "1"),
            (2.0,  "2"),
            (4.0,  "4"),
            (6.0,  "6"),
            (8.0,  "8"),
            (10.0, "10"),
        };

        int seed = 20260530;
        foreach (var (c, suffix) in stops)
        {
            var cGPerMl = c * 1e-3;
            var apparentDh = dHydroAtZero / (1.0 + kD * cGPerMl);
            // The cumulant fit will see broader peaks at higher concentration
            // because of the extra polydispersity from interaction-induced
            // distributions; encode that in the lognormal width.
            var pdi = c < 2.0 ? 0.08 : (c < 6.0 ? 0.10 : 0.13);

            yield return new DlsSyntheticSheet
            {
                SheetName = $"PNIPAM_conc_{suffix}mgmL",
                SampleLabel = $"PNIPAM conc {c.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}mg/mL",
                TemperatureCelsius = 25.0,
                ViscosityMpas = 0.890,
                RefractiveIndex = 1.330,
                WavelengthNm = 633.0,
                ScatteringAngleDegrees = 173.0,
                Populations = new[]
                {
                    new DlsPopulation(DiameterNm: apparentDh, IntensityWeight: 1.0, PolydispersityIndex: pdi),
                },
                Seed = seed++,
            };
        }
    }

    public static void WriteDemoWorkbook(string filePath)
    {
        using var workbook = new XLWorkbook();
        AddSheet(workbook, PnipamCoilAt25C);
        AddSheet(workbook, PnipamGlobuleAt35C);
        foreach (var rampSheet in PnipamTemperatureRamp())
            AddSheet(workbook, rampSheet);
        foreach (var concSheet in PnipamConcentrationSeries())
            AddSheet(workbook, concSheet);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        workbook.SaveAs(filePath);
    }

    private static void AddSheet(XLWorkbook workbook, DlsSyntheticSheet recipe)
    {
        var ws = workbook.AddWorksheet(recipe.SheetName);
        var rng = new Random(recipe.Seed);

        var sizeBins = Physics.Logspace(recipe.SizeMinNm, recipe.SizeMaxNm, recipe.SizeBinCount);
        var times = Physics.Logspace(recipe.TimeMinMicroseconds, recipe.TimeMaxMicroseconds, recipe.CorrelationPointCount);

        // Normalise intensity weights so the populations sum to 1; this
        // matches how a real Zetasizer reports relative scattering
        // contributions across populations.
        var totalWeight = recipe.Populations.Sum(p => p.IntensityWeight);
        var intensityWeights = recipe.Populations.Select(p => p.IntensityWeight / totalWeight).ToArray();

        // Per population, compute Gamma_i (mu^-1) only. The cumulant
        // expansion that motivates mu2 is only valid in the short-tau
        // limit; using it as a long-tau extrapolant blows up because
        // the (mu2/2) tau^2 term eventually dominates -Gamma tau and
        // pulls g1 above 1. Polydispersity is instead encoded in the
        // distribution shapes (lognormal width derived from PdI), and
        // the ACF here is a clean sum of single exponentials. The
        // CumulantAnalyzer still recovers a sensible Gamma from this.
        var gammas = new double[recipe.Populations.Count];
        for (int i = 0; i < recipe.Populations.Count; i++)
        {
            var pop = recipe.Populations[i];
            gammas[i] = Physics.FirstCumulantPerMicrosecond(
                pop.DiameterNm,
                recipe.TemperatureCelsius,
                recipe.ViscosityMpas,
                recipe.RefractiveIndex,
                recipe.WavelengthNm,
                recipe.ScatteringAngleDegrees);
        }

        // Build the size distribution shapes:
        //   Number(d)    proportional to lognormal(d) weighted by N_i
        //   Volume(d)    proportional to Number(d) * d^3
        //   Intensity(d) proportional to Number(d) * d^6 (Rayleigh limit)
        // Lognormal sigma is derived from PdI using the standard relation
        // sigma^2 = ln(1 + PdI), which is the same convention Zetasizer
        // ships in its software documentation.
        var numberCurve = new double[recipe.SizeBinCount];
        for (int i = 0; i < recipe.SizeBinCount; i++)
        {
            double sum = 0;
            for (int p = 0; p < recipe.Populations.Count; p++)
            {
                var pop = recipe.Populations[p];
                var sigma = Math.Sqrt(Math.Log(1.0 + Math.Max(pop.PolydispersityIndex, 1e-4)));
                var pdf = Physics.LognormalPdf(sizeBins[i], pop.DiameterNm, sigma);
                // N_i derived so that Intensity weight matches the recipe:
                // I_i ~ N_i * d_i^6 (single-particle scaling), so
                // N_i = w_intensity_i / d_i^6.
                var numberWeight = intensityWeights[p] / Math.Pow(pop.DiameterNm, 6);
                sum += numberWeight * pdf;
            }
            numberCurve[i] = sum;
        }

        var volumeCurve = new double[recipe.SizeBinCount];
        var intensityCurve = new double[recipe.SizeBinCount];
        for (int i = 0; i < recipe.SizeBinCount; i++)
        {
            var d = sizeBins[i];
            volumeCurve[i] = numberCurve[i] * d * d * d;
            intensityCurve[i] = numberCurve[i] * Math.Pow(d, 6);
        }

        Normalise(numberCurve);
        Normalise(volumeCurve);
        Normalise(intensityCurve);

        // Layout matches the WriteFullExport fixture in the test suite:
        // three Size+Y blocks per distribution kind (Number / Intensity /
        // Volume) for runCount runs, separated by single empty columns,
        // followed by one Time + Correlation block per run.
        int col = 1;
        col = WriteDistributionRuns(ws, col, recipe, sizeBins, numberCurve, "Number", rng);
        col++;
        col = WriteDistributionRuns(ws, col, recipe, sizeBins, intensityCurve, "Intensity", rng);
        col++;
        col = WriteDistributionRuns(ws, col, recipe, sizeBins, volumeCurve, "Volume", rng);
        col++;
        WriteCorrelationRuns(ws, col, recipe, times, gammas, intensityWeights, rng);

        ws.Columns().AdjustToContents();
    }

    private static int WriteDistributionRuns(
        IXLWorksheet ws,
        int startCol,
        DlsSyntheticSheet recipe,
        double[] sizeBins,
        double[] meanCurve,
        string distributionKind,
        Random rng)
    {
        var col = startCol;
        for (int run = 0; run < recipe.RunCount; run++)
        {
            var xHeader = $"Size (d.nm) - {recipe.SampleLabel} [Steady state]";
            var yHeader = $"{distributionKind} (Percent) - {recipe.SampleLabel} [Steady state]";
            ws.Cell(1, col).Value = xHeader;
            ws.Cell(1, col + 1).Value = yHeader;

            // Apply a small per-run multiplicative jitter (+-2%) so the
            // three runs are not byte-identical, matching how a real
            // Zetasizer dataset looks.
            var perRun = new double[meanCurve.Length];
            double sum = 0;
            for (int i = 0; i < meanCurve.Length; i++)
            {
                var jitter = 1.0 + 0.02 * (rng.NextDouble() * 2 - 1);
                perRun[i] = Math.Max(meanCurve[i] * jitter, 0);
                sum += perRun[i];
            }
            if (sum > 0)
                for (int i = 0; i < perRun.Length; i++) perRun[i] = perRun[i] * 100.0 / sum;

            for (int i = 0; i < sizeBins.Length; i++)
            {
                ws.Cell(i + 2, col).Value = Physics.Round(sizeBins[i], 4);
                ws.Cell(i + 2, col + 1).Value = Physics.Round(perRun[i], 4);
            }
            col += 2;
        }
        return col;
    }

    private static void WriteCorrelationRuns(
        IXLWorksheet ws,
        int startCol,
        DlsSyntheticSheet recipe,
        double[] times,
        double[] gammas,
        double[] intensityWeights,
        Random rng)
    {
        var col = startCol;
        for (int run = 0; run < recipe.RunCount; run++)
        {
            var xHeader = $"Time (µs) - {recipe.SampleLabel} [Steady state]";
            var yHeader = $"Correlation Coefficient (g₂-1) - {recipe.SampleLabel} [Steady state]";
            ws.Cell(1, col).Value = xHeader;
            ws.Cell(1, col + 1).Value = yHeader;

            for (int i = 0; i < times.Length; i++)
            {
                var tau = times[i];

                // g1(tau) = sum_k w_k * exp(-Gamma_k * tau)
                // Pure exponential sum; well-behaved at all tau and the
                // textbook starting point for cumulant analysis.
                double g1 = 0;
                for (int k = 0; k < gammas.Length; k++)
                {
                    g1 += intensityWeights[k] * Math.Exp(-gammas[k] * tau);
                }

                var g2Minus1 = recipe.Beta * g1 * g1;

                // Add Gaussian noise scaled by a tau-dependent factor:
                // baseline noise dominates the long-tau tail, the short
                // tau side is essentially noise-free.
                var noiseScale = recipe.NoiseSigma * (0.3 + 0.7 * Math.Min(tau / times[^1], 1.0));
                var noise = SampleGaussian(rng) * noiseScale;
                var value = g2Minus1 + noise;

                ws.Cell(i + 2, col).Value = Physics.Round(tau, 4);
                ws.Cell(i + 2, col + 1).Value = Physics.Round(value, 5);
            }
            col += 2;
        }
    }

    private static double SampleGaussian(Random rng)
    {
        // Box-Muller. Re-using the cached second sample is unnecessary
        // here because the generator only runs once at build time.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static void Normalise(double[] curve)
    {
        double sum = 0;
        for (int i = 0; i < curve.Length; i++) sum += curve[i];
        if (sum <= 0) return;
        var scale = 100.0 / sum;
        for (int i = 0; i < curve.Length; i++) curve[i] *= scale;
    }
}
