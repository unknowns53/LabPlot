namespace DlsAnalyzer.Core;

/// <summary>
/// One bin of an inverted DLS size distribution. Exposes the diameter
/// of the bin centre plus the three customary scaling weights so that
/// the UI can plot any of "Intensity (raw NNLS solution) / Number /
/// Volume" without recomputing the kernel.
/// </summary>
public sealed record SizeDistributionInversionBin(
    double DiameterNm,
    double IntensityWeight,
    double NumberWeight,
    double VolumeWeight);

/// <summary>
/// Result of a successful Tikhonov-regularised NNLS inversion of the
/// autocorrelation function into a discrete particle-size distribution.
/// </summary>
/// <remarks>
/// The inverter solves
///     min || K·x − y ||₂²  +  α² · || L·x ||₂²    s.t.  x ≥ 0
/// where
///     y_i = |g₁(τ_i)| = √(max(g₂(τ_i) − 1, 0) / β)
///     K[i,j] = exp(−Γ_j · τ_i)
///     L      = second-order difference operator (smoothness prior)
/// Γ_j is the relaxation rate of bin j derived from the bin diameter
/// via Stokes–Einstein, and the bin grid is log-spaced between the
/// supplied bounds. β is recovered from the smallest-τ samples.
/// </remarks>
public sealed record SizeDistributionInversionResult
{
    public required IReadOnlyList<SizeDistributionInversionBin> Bins { get; init; }
    public required double Beta { get; init; }
    public required double RegularizationAlpha { get; init; }
    public required double ResidualNormSquared { get; init; }
    public required double SolutionRoughnessSquared { get; init; }
    public required int OuterIterations { get; init; }
    public required int FreeBinCount { get; init; }
    /// <summary>R² in the |g₁| domain comparing K·x against the y vector.</summary>
    public required double RSquared { get; init; }
    public required int UsedTimeSampleCount { get; init; }
}

/// <summary>
/// Outcome wrapper letting the UI render structured failure messages
/// (missing metadata, no positive g₂-1 samples, NNLS divergence) the
/// same way the cumulant and ramp analyzers do.
/// </summary>
public sealed record SizeDistributionInversionOutcome
{
    public required bool Success { get; init; }
    public SizeDistributionInversionResult? Result { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();

    public static SizeDistributionInversionOutcome Ok(SizeDistributionInversionResult result)
        => new() { Success = true, Result = result };

    public static SizeDistributionInversionOutcome Fail(string reason)
        => new() { Success = false, FailureReason = reason };

    public static SizeDistributionInversionOutcome Missing(IReadOnlyList<string> missing)
        => new() { Success = false, MissingFields = missing, FailureReason = "測定条件が不足しています" };
}

/// <summary>
/// Options controlling the diameter grid and regularisation strategy
/// used by <see cref="SizeDistributionInverter"/>.
/// </summary>
public sealed record SizeDistributionInverterOptions
{
    /// <summary>Number of log-spaced d_h bins. Defaults to 60.</summary>
    public int BinCount { get; init; } = 60;
    /// <summary>Lower bound of the d_h grid (nm).</summary>
    public double MinDiameterNm { get; init; } = 0.4;
    /// <summary>Upper bound of the d_h grid (nm).</summary>
    public double MaxDiameterNm { get; init; } = 10_000.0;
    /// <summary>
    /// Manual regularisation strength. When null, the inverter sweeps a
    /// log-spaced grid between <see cref="AutoAlphaMin"/> and
    /// <see cref="AutoAlphaMax"/> and picks the L-curve corner.
    /// </summary>
    public double? RegularizationAlpha { get; init; } = null;
    public int AutoAlphaCandidateCount { get; init; } = 16;
    public double AutoAlphaMin { get; init; } = 1e-4;
    public double AutoAlphaMax { get; init; } = 1.0;
    /// <summary>g₂-1 noise floor — samples below this are treated as baseline and dropped.</summary>
    public double SignalThreshold { get; init; } = 1e-3;
    /// <summary>Number of leading τ samples averaged to estimate β.</summary>
    public int BetaEstimationSampleCount { get; init; } = 5;
}

/// <summary>
/// Tikhonov-regularised NNLS inverter that turns an autocorrelation
/// function into a discrete particle size distribution. Equivalent in
/// spirit to CONTIN's intensity-weighted distribution mode but with a
/// fixed second-order smoothness prior and an L-curve corner picker
/// for α rather than CONTIN's Bayesian probability machinery.
/// </summary>
public static class SizeDistributionInverter
{
    private const double BoltzmannJoulePerKelvin = StokesEinstein.BoltzmannJoulePerKelvin;

    public static SizeDistributionInversionOutcome Invert(
        CorrelationFunction? correlation,
        double? temperatureCelsius,
        double? viscosityMpas,
        double? refractiveIndex,
        double? wavelengthNm,
        double? scatteringAngleDegrees,
        SizeDistributionInverterOptions? options = null)
    {
        var opts = options ?? new SizeDistributionInverterOptions();

        // ---- metadata gate (mirrors StokesEinstein.Compute) ---------------
        var missing = new List<string>();
        if (!IsPositive(temperatureCelsius is double t ? t + 273.15 : null)) missing.Add("温度");
        if (!IsPositive(viscosityMpas)) missing.Add("粘度");
        if (!IsPositive(refractiveIndex)) missing.Add("屈折率");
        if (!IsPositive(wavelengthNm)) missing.Add("波長");
        if (scatteringAngleDegrees is null
            || !double.IsFinite(scatteringAngleDegrees.Value)
            || scatteringAngleDegrees.Value <= 0
            || scatteringAngleDegrees.Value > 180)
        {
            // Physical DLS scattering angles are in (0, 180]. Values above
            // 180° pass sin(θ/2) > 0 mathematically but represent unreachable
            // geometry; reject them in line with StokesEinstein.IsValidAngle.
            missing.Add("散乱角");
        }
        if (missing.Count > 0)
            return SizeDistributionInversionOutcome.Missing(missing);

        if (correlation is null)
            return SizeDistributionInversionOutcome.Fail("自己相関データがありません");

        var times = correlation.TimesMicroseconds;
        var values = correlation.ActiveRun;
        int pairCount = Math.Min(times.Count, values.Count);
        if (pairCount < 8)
            return SizeDistributionInversionOutcome.Fail("自己相関の点数が不足しています");

        if (opts.BinCount < 4)
            return SizeDistributionInversionOutcome.Fail("ビン数が不足しています");
        if (!(opts.MinDiameterNm > 0) || !(opts.MaxDiameterNm > opts.MinDiameterNm)
            || !double.IsFinite(opts.MinDiameterNm) || !double.IsFinite(opts.MaxDiameterNm))
            return SizeDistributionInversionOutcome.Fail("粒径グリッドが不正です");

        // Validate auto-α sweep bounds before LogSpace consumes them so a
        // NaN/Inf or non-positive range cannot propagate into the NNLS solve.
        if (opts.RegularizationAlpha is null)
        {
            if (!double.IsFinite(opts.AutoAlphaMin) || !double.IsFinite(opts.AutoAlphaMax)
                || !(opts.AutoAlphaMin > 0) || !(opts.AutoAlphaMax > opts.AutoAlphaMin))
                return SizeDistributionInversionOutcome.Fail("自動α範囲が不正です");
        }

        // ---- collect (τ, g₂-1) pairs and estimate β -----------------------
        // Sort the (τ, g₂-1) pairs by τ ascending so the "smallest-τ" β
        // estimator always sees the actual smallest-τ samples regardless
        // of input ordering. The CorrelationFunction type contract does
        // not guarantee τ-ascending data — only the Zetasizer xlsx export
        // happens to emit it that way — so an upstream reader change
        // could otherwise silently break β recovery.
        var pairs = new List<(double Tau, double G)>(pairCount);
        for (int i = 0; i < pairCount; i++)
        {
            var tau = times[i];
            var g = values[i];
            if (!double.IsFinite(tau) || tau <= 0) continue;
            if (!double.IsFinite(g)) continue;
            pairs.Add((tau, g));
        }
        if (pairs.Count < 8)
            return SizeDistributionInversionOutcome.Fail("自己相関の有効点数が不足しています");

        pairs.Sort(static (a, b) => a.Tau.CompareTo(b.Tau));

        // β ≈ peak of g₂-1, taken as the median of the few smallest-τ
        // samples. Real Zetasizer traces are noisy at τ=0 so a single
        // sample would over- or under-shoot; the median across a small
        // window is robust against either outlier.
        var betaSampleCount = Math.Clamp(opts.BetaEstimationSampleCount, 1, pairs.Count);
        var earlySamples = new List<double>(betaSampleCount);
        for (int i = 0; i < betaSampleCount; i++) earlySamples.Add(pairs[i].G);
        earlySamples.Sort();
        var beta = earlySamples[earlySamples.Count / 2];
        if (!double.IsFinite(beta) || beta <= 0)
            return SizeDistributionInversionOutcome.Fail("β を推定できません（g₂-1 の初期値が非物理的）");

        // y = |g₁(τ)| = √(max(g₂-1, 0) / β); samples whose g₂-1 falls below
        // the noise floor get dropped because the sqrt would amplify
        // baseline noise.
        var yValues = new List<double>(pairs.Count);
        var yTaus = new List<double>(pairs.Count);
        for (int i = 0; i < pairs.Count; i++)
        {
            var g = pairs[i].G;
            if (g < opts.SignalThreshold) continue;
            var ratio = g / beta;
            if (ratio <= 0) continue;
            // Clamp ratio at 1 + epsilon to keep the sqrt well-defined when
            // the smallest-τ sample marginally overshoots the median β.
            ratio = Math.Min(ratio, 1.0 + 1e-3);
            yValues.Add(Math.Sqrt(Math.Max(0, ratio)));
            yTaus.Add(pairs[i].Tau);
        }
        if (yValues.Count < 8)
            return SizeDistributionInversionOutcome.Fail("シグナル点数が不足しています");

        int m = yValues.Count;
        int n = opts.BinCount;

        // ---- diameter grid → Γ grid via Stokes-Einstein -------------------
        var diameters = LogSpace(opts.MinDiameterNm, opts.MaxDiameterNm, n);
        var gammas = new double[n];
        var tKelvin = temperatureCelsius!.Value + 273.15;
        var etaPa = viscosityMpas!.Value * 1e-3;
        var lambdaMeter = wavelengthNm!.Value * 1e-9;
        var thetaRad = scatteringAngleDegrees!.Value * Math.PI / 180.0;
        var q = (4.0 * Math.PI * refractiveIndex!.Value / lambdaMeter) * Math.Sin(thetaRad / 2.0);
        var qSquared = q * q;
        for (int j = 0; j < n; j++)
        {
            var dMeter = diameters[j] * 1e-9;
            var dDiff = BoltzmannJoulePerKelvin * tKelvin / (3.0 * Math.PI * etaPa * dMeter);
            // Γ in s⁻¹ then converted to μs⁻¹ to match the τ axis.
            gammas[j] = dDiff * qSquared * 1e-6;
        }

        // ---- kernel K[i,j] = exp(-Γ_j τ_i) -------------------------------
        var kernel = new double[m, n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
                kernel[i, j] = Math.Exp(-gammas[j] * yTaus[i]);

        // ---- second-order difference smoothness operator (n-2 × n) -------
        // Penalising L·x squared promotes smoothness in the j direction
        // (logarithmic spacing in d) and breaks the degeneracy that lets
        // NNLS pile mass into single bins.
        int lRows = Math.Max(0, n - 2);
        var lOperator = new double[lRows, n];
        for (int r = 0; r < lRows; r++)
        {
            lOperator[r, r] = 1.0;
            lOperator[r, r + 1] = -2.0;
            lOperator[r, r + 2] = 1.0;
        }

        // ---- regularisation parameter selection --------------------------
        double[] alphaCandidates;
        if (opts.RegularizationAlpha is double fixedAlpha && fixedAlpha > 0)
        {
            alphaCandidates = new[] { fixedAlpha };
        }
        else
        {
            int candidateCount = Math.Max(3, opts.AutoAlphaCandidateCount);
            alphaCandidates = LogSpace(opts.AutoAlphaMin, opts.AutoAlphaMax, candidateCount);
        }

        var bestSolution = new double[n];
        double bestAlpha = double.NaN;
        double bestResidual = double.PositiveInfinity;
        double bestRoughness = double.PositiveInfinity;
        int bestOuter = 0;
        int bestFreeBins = 0;

        // L-curve corner picker: log(residual²) vs log(roughness²).
        // Maximum positive curvature on the discrete curve marks the knee.
        var residualLogs = new double[alphaCandidates.Length];
        var roughnessLogs = new double[alphaCandidates.Length];
        var solutions = new double[alphaCandidates.Length][];
        var residuals = new double[alphaCandidates.Length];
        var roughnesses = new double[alphaCandidates.Length];
        var outerIterations = new int[alphaCandidates.Length];
        var convergedFlags = new bool[alphaCandidates.Length];

        for (int c = 0; c < alphaCandidates.Length; c++)
        {
            var alpha = alphaCandidates[c];
            var (sol, residSq, roughSq, outerCount, converged) = SolveTikhonovNnls(kernel, yValues, lOperator, alpha);
            solutions[c] = sol;
            residuals[c] = residSq;
            roughnesses[c] = roughSq;
            outerIterations[c] = outerCount;
            convergedFlags[c] = converged;
            residualLogs[c] = Math.Log(Math.Max(residSq, 1e-30));
            roughnessLogs[c] = Math.Log(Math.Max(roughSq, 1e-30));
        }

        // Restrict the L-curve picker to candidates whose NNLS sub-solve
        // actually converged. A non-converged subproblem still emits a
        // finite ResidualSquared (the partial solution at bailout), so
        // ignoring Converged would silently elevate failed fits.
        var convergedIndices = new List<int>(alphaCandidates.Length);
        for (int c = 0; c < alphaCandidates.Length; c++)
            if (convergedFlags[c]) convergedIndices.Add(c);

        if (convergedIndices.Count == 0)
            return SizeDistributionInversionOutcome.Fail("NNLS が収束しませんでした（全 α 候補）");

        int chosenIndex;
        if (convergedIndices.Count == 1)
        {
            chosenIndex = convergedIndices[0];
        }
        else
        {
            var subResLogs = new double[convergedIndices.Count];
            var subRoughLogs = new double[convergedIndices.Count];
            for (int k = 0; k < convergedIndices.Count; k++)
            {
                subResLogs[k] = residualLogs[convergedIndices[k]];
                subRoughLogs[k] = roughnessLogs[convergedIndices[k]];
            }
            var subChosen = PickLCurveCorner(subResLogs, subRoughLogs);
            chosenIndex = convergedIndices[subChosen];
        }

        bestAlpha = alphaCandidates[chosenIndex];
        bestSolution = solutions[chosenIndex];
        bestResidual = residuals[chosenIndex];
        bestRoughness = roughnesses[chosenIndex];
        bestOuter = outerIterations[chosenIndex];
        bestFreeBins = 0;
        for (int j = 0; j < n; j++) if (bestSolution[j] > 0) bestFreeBins++;

        if (!double.IsFinite(bestResidual))
            return SizeDistributionInversionOutcome.Fail("NNLS が収束しませんでした");

        // R² in the |g₁| domain.
        double meanY = 0;
        for (int i = 0; i < m; i++) meanY += yValues[i];
        meanY /= m;
        double ssTot = 0;
        for (int i = 0; i < m; i++)
        {
            var dy = yValues[i] - meanY;
            ssTot += dy * dy;
        }
        var rSquared = ssTot > 0 ? 1.0 - bestResidual / ssTot : 1.0;

        // Convert intensity-weighted bins to number / volume distributions
        // and normalise each so the sum is 100 (matching how
        // ParticleSizeDistribution surfaces percentages everywhere else).
        var intensitySum = 0.0;
        var numberRaw = new double[n];
        var volumeRaw = new double[n];
        var intensityRaw = new double[n];
        for (int j = 0; j < n; j++)
        {
            var d = diameters[j];
            intensityRaw[j] = bestSolution[j];
            numberRaw[j] = bestSolution[j] / Math.Pow(d, 6);
            volumeRaw[j] = bestSolution[j] / Math.Pow(d, 3);
            intensitySum += intensityRaw[j];
        }
        Normalise(intensityRaw);
        Normalise(numberRaw);
        Normalise(volumeRaw);

        var bins = new SizeDistributionInversionBin[n];
        for (int j = 0; j < n; j++)
        {
            bins[j] = new SizeDistributionInversionBin(
                DiameterNm: diameters[j],
                IntensityWeight: intensityRaw[j],
                NumberWeight: numberRaw[j],
                VolumeWeight: volumeRaw[j]);
        }

        return SizeDistributionInversionOutcome.Ok(new SizeDistributionInversionResult
        {
            Bins = bins,
            Beta = beta,
            RegularizationAlpha = bestAlpha,
            ResidualNormSquared = bestResidual,
            SolutionRoughnessSquared = bestRoughness,
            OuterIterations = bestOuter,
            FreeBinCount = bestFreeBins,
            RSquared = rSquared,
            UsedTimeSampleCount = m,
        });
    }

    /// <summary>
    /// Solve min ||K·x − y||² + α² ||L·x||²  s.t. x ≥ 0 by stacking the
    /// regularisation rows below K and feeding the augmented system to
    /// <see cref="Nnls"/>.
    /// </summary>
    private static (double[] Solution, double ResidualSquared, double RoughnessSquared, int OuterIterations, bool Converged)
        SolveTikhonovNnls(double[,] kernel, IReadOnlyList<double> y, double[,] lOperator, double alpha)
    {
        int m = kernel.GetLength(0);
        int n = kernel.GetLength(1);
        int lr = lOperator.GetLength(0);

        var aug = new double[m + lr, n];
        var bug = new double[m + lr];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = kernel[i, j];
            bug[i] = y[i];
        }
        for (int i = 0; i < lr; i++)
        {
            for (int j = 0; j < n; j++) aug[m + i, j] = alpha * lOperator[i, j];
            // bug[m + i] already 0 (Tikhonov target on smoothness rows)
        }

        var nnlsOut = Nnls.Solve(aug, bug);
        // Compute the data-fit residual and the smoothness penalty
        // separately so the L-curve picker can see the trade-off.
        double residSq = 0;
        for (int i = 0; i < m; i++)
        {
            double sum = y[i];
            for (int j = 0; j < n; j++) sum -= kernel[i, j] * nnlsOut.X[j];
            residSq += sum * sum;
        }
        double roughSq = 0;
        for (int i = 0; i < lr; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++) sum += lOperator[i, j] * nnlsOut.X[j];
            roughSq += sum * sum;
        }
        // Propagate NNLS convergence: the caller filters non-converged
        // candidates out of the L-curve corner picker so a singular
        // sub-solve never silently graduates to a published distribution.
        return (nnlsOut.X, residSq, roughSq, nnlsOut.OuterIterations, nnlsOut.Converged);
    }

    /// <summary>
    /// Pick the index whose (log residual, log roughness) point sits
    /// closest to the L-curve corner. Uses a discrete-curvature
    /// approximation; ties default to the geometric midpoint of the α
    /// grid which empirically gives a sensible "slightly-smooth" fit.
    /// </summary>
    private static int PickLCurveCorner(double[] residualLogs, double[] roughnessLogs)
    {
        int n = residualLogs.Length;
        if (n <= 2) return n / 2;

        // Triangle-area curvature estimate à la Hansen 2007: the area of
        // the triangle spanned by three consecutive points on the L-curve
        // is proportional to the local curvature times the segment length.
        // The maximum-area point is the corner.
        int bestIdx = n / 2;
        double bestScore = double.NegativeInfinity;
        for (int i = 1; i < n - 1; i++)
        {
            var x1 = residualLogs[i - 1]; var y1 = roughnessLogs[i - 1];
            var x2 = residualLogs[i];     var y2 = roughnessLogs[i];
            var x3 = residualLogs[i + 1]; var y3 = roughnessLogs[i + 1];
            // Twice the signed area of the triangle.
            var area = Math.Abs((x2 - x1) * (y3 - y1) - (x3 - x1) * (y2 - y1));
            // Penalise the over-smooth and over-rough endpoints by
            // requiring the middle point to be "below the chord" — i.e.
            // residual smaller and roughness smaller than the chord
            // midpoint expects. This rules out flat tails of the L-curve
            // where the area calculation is dominated by floating-point
            // jitter.
            var midX = 0.5 * (x1 + x3);
            var midY = 0.5 * (y1 + y3);
            if (x2 > midX && y2 > midY) continue;
            if (area > bestScore)
            {
                bestScore = area;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private static double[] LogSpace(double min, double max, int count)
    {
        var values = new double[count];
        var logMin = Math.Log10(min);
        var logMax = Math.Log10(max);
        for (int i = 0; i < count; i++)
            values[i] = Math.Pow(10, logMin + (logMax - logMin) * i / (count - 1));
        return values;
    }

    private static void Normalise(double[] values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        if (sum <= 0) return;
        var scale = 100.0 / sum;
        for (int i = 0; i < values.Length; i++) values[i] *= scale;
    }

    private static bool IsPositive(double? v) => v.HasValue && double.IsFinite(v.Value) && v.Value > 0;
}
