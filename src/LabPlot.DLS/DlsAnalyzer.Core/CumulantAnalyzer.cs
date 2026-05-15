namespace DlsAnalyzer.Core;

/// <summary>
/// Result of a successful cumulant fit on an autocorrelation function.
/// </summary>
/// <remarks>
/// Convention follows the standard cumulants expansion
///     ln|g₁(τ)| ≈ -Γ·τ + (μ₂/2)·τ² - …
/// translated to g₂(τ)-1 via the Siegert relation g₂-1 = β·|g₁|², so the
/// quadratic fit y = a₀ + a₁·τ + a₂·τ² on y = ln(g₂-1) carries
/// a₁ = -2Γ and a₂ = μ₂. Time units are μs because Zetasizer xlsx
/// stores delay times in μs; Γ comes out in μs⁻¹ and μ₂ in μs⁻².
/// </remarks>
public sealed record CumulantResult
{
    public required double FirstCumulantPerMicrosecond { get; init; }
    public required double SecondCumulantPerMicrosecondSquared { get; init; }
    public required double PolydispersityIndex { get; init; }
    public required double RSquared { get; init; }
    public required double AppliedRangeMinMicroseconds { get; init; }
    public required double AppliedRangeMaxMicroseconds { get; init; }
    public required int PointCount { get; init; }
    /// <summary>
    /// True when the fitted μ₂ came out negative. PdI is clamped to
    /// max(μ₂, 0) / Γ² for display, but the negative-μ₂ signal itself
    /// flags low fit quality (noisy data, wrong τ window, monomer
    /// contamination). UI can surface this as a warning badge.
    /// </summary>
    public bool Mu2WasNegative { get; init; }
}

/// <summary>
/// Outcome wrapper distinguishing a successful fit from a structured
/// failure (no correlation data, too few usable points, degenerate
/// matrix, etc.). The UI shows <see cref="FailureReason"/> verbatim
/// when <see cref="Success"/> is false.
/// </summary>
public sealed record CumulantOutcome
{
    public required bool Success { get; init; }
    public CumulantResult? Result { get; init; }
    public string? FailureReason { get; init; }

    public static CumulantOutcome Ok(CumulantResult result)
        => new() { Success = true, Result = result };

    public static CumulantOutcome Fail(string reason)
        => new() { Success = false, FailureReason = reason };
}

/// <summary>
/// Quadratic least-squares cumulant fit for DLS autocorrelation data.
/// </summary>
public static class CumulantAnalyzer
{
    /// <summary>
    /// Default lower bound on g₂-1 used when both range endpoints are
    /// null. Points below this threshold are treated as the baseline /
    /// noise floor and excluded from the fit.
    /// </summary>
    public const double DefaultAutoThreshold = 0.1;

    /// <summary>Minimum number of points required to fit a quadratic.</summary>
    public const int MinimumPointCount = 4;

    /// <summary>
    /// Fit ln(g₂-1) over the chosen range as a quadratic in τ and
    /// extract Γ, μ₂, and the polydispersity index.
    /// </summary>
    /// <param name="correlation">Source correlation function. Null returns failure.</param>
    /// <param name="minMicroseconds">Lower bound of the τ window. Null = auto-detect.</param>
    /// <param name="maxMicroseconds">Upper bound of the τ window. Null = auto-detect.</param>
    /// <param name="autoThreshold">g₂-1 cutoff used when both bounds are null.</param>
    public static CumulantOutcome Analyze(
        CorrelationFunction? correlation,
        double? minMicroseconds = null,
        double? maxMicroseconds = null,
        double autoThreshold = DefaultAutoThreshold)
    {
        if (correlation is null)
            return CumulantOutcome.Fail("自己相関データがありません");

        // Public API: gate NaN / Infinity on the τ window and threshold
        // before they reach the comparison logic. NaN comparisons always
        // return false, which would silently disable the corresponding
        // bound and slide into the auto-threshold path with no diagnostic.
        if (minMicroseconds.HasValue && !double.IsFinite(minMicroseconds.Value))
            return CumulantOutcome.Fail("τ 下限が不正です (NaN/Inf)");
        if (maxMicroseconds.HasValue && !double.IsFinite(maxMicroseconds.Value))
            return CumulantOutcome.Fail("τ 上限が不正です (NaN/Inf)");
        if (minMicroseconds.HasValue && maxMicroseconds.HasValue
            && minMicroseconds.Value >= maxMicroseconds.Value)
            return CumulantOutcome.Fail("τ 下限が τ 上限以上です");
        if (!double.IsFinite(autoThreshold))
            return CumulantOutcome.Fail("自動 threshold が不正です (NaN/Inf)");

        var times = correlation.TimesMicroseconds;
        var values = correlation.ActiveRun;
        var pairCount = Math.Min(times.Count, values.Count);
        if (pairCount == 0)
            return CumulantOutcome.Fail("自己相関データがありません");

        // Collect candidate (τ, g₂-1) points: τ must be positive (log
        // domain requirement) and g₂-1 must be positive (ln domain).
        // Negative noise tails are excluded automatically by the second
        // check.
        var taus = new List<double>(pairCount);
        var ys = new List<double>(pairCount);
        for (int i = 0; i < pairCount; i++)
        {
            var tau = times[i];
            var g = values[i];
            if (!double.IsFinite(tau) || !double.IsFinite(g)) continue;
            if (tau <= 0 || g <= 0) continue;
            taus.Add(tau);
            ys.Add(g);
        }

        if (taus.Count < MinimumPointCount)
            return CumulantOutcome.Fail("有効な点数が不足しています");

        // Apply explicit bounds if either is supplied; otherwise apply
        // the auto-threshold (g₂-1 ≥ threshold) to drop the noise tail.
        // Mixed bounds (one supplied, one null) keep the supplied side
        // tight and let the auto-rule decide the other side.
        bool autoMin = !minMicroseconds.HasValue;
        bool autoMax = !maxMicroseconds.HasValue;
        bool useContiguous = autoMin || autoMax;

        // When the auto-threshold drives at least one bound, walk τ in
        // ascending order and stop at the FIRST sample that falls below the
        // threshold. The previous "drop any individual point below
        // threshold" rule kept post-noise recoveries — a single g=0.05 dip
        // followed by g=0.15 at the next τ would have re-included the
        // later point, which usually represents random correlator noise
        // around the baseline rather than a genuine second decay. The
        // contiguous-window behaviour mirrors what an operator does by
        // hand when picking the fit range visually.
        var ordered = new int[taus.Count];
        for (int i = 0; i < taus.Count; i++) ordered[i] = i;
        Array.Sort(ordered, (a, b) => taus[a].CompareTo(taus[b]));

        var keptTaus = new List<double>(taus.Count);
        var keptYs = new List<double>(taus.Count);
        bool started = false;
        foreach (var idx in ordered)
        {
            var tau = taus[idx];
            var g = ys[idx];
            if (!autoMin && tau < minMicroseconds!.Value) continue;
            if (!autoMax && tau > maxMicroseconds!.Value) continue;

            if (useContiguous)
            {
                var aboveThreshold = g >= autoThreshold;
                if (!started)
                {
                    if (!aboveThreshold) continue;
                    started = true;
                }
                else if (!aboveThreshold)
                {
                    break;
                }
            }

            keptTaus.Add(tau);
            keptYs.Add(Math.Log(g));
        }

        if (keptTaus.Count < MinimumPointCount)
            return CumulantOutcome.Fail($"有効な点数が不足しています（{keptTaus.Count}/{MinimumPointCount} 点）");

        if (!TrySolveQuadratic(keptTaus, keptYs, out var a0, out var a1, out var a2))
            return CumulantOutcome.Fail("係数行列が特異で fit できません");

        // a₁ = -2Γ → Γ = -a₁/2; a₂ = μ₂ directly.
        var gamma = -a1 / 2.0;
        var mu2 = a2;

        // Reject unphysical fits: Γ must be positive (decay, not growth).
        // μ₂ may legitimately be slightly negative on noisy data, but
        // PdI is reported as max(μ₂, 0) / Γ² so the displayed value
        // never becomes negative.
        if (!double.IsFinite(gamma) || gamma <= 0)
            return CumulantOutcome.Fail("Γ が非物理的です（負または非有限）");

        var pdi = Math.Max(mu2, 0) / (gamma * gamma);

        // R² on the linear-domain fit (y = ln(g₂-1) space).
        double sumY = 0;
        for (int i = 0; i < keptYs.Count; i++) sumY += keptYs[i];
        var meanY = sumY / keptYs.Count;
        double ssTot = 0, ssRes = 0;
        for (int i = 0; i < keptYs.Count; i++)
        {
            var tau = keptTaus[i];
            var yhat = a0 + a1 * tau + a2 * tau * tau;
            var dy = keptYs[i] - meanY;
            var er = keptYs[i] - yhat;
            ssTot += dy * dy;
            ssRes += er * er;
        }
        var rSquared = ssTot > 0 ? 1.0 - ssRes / ssTot : 1.0;

        // keptTaus retains the input ordering of correlation.TimesMicroseconds
        // — Zetasizer xlsx exports are τ-ascending in practice but the type
        // contract does not guarantee it. Compute min/max explicitly so the
        // reported range stays correct regardless of source ordering.
        double rangeMin = keptTaus[0], rangeMax = keptTaus[0];
        for (int i = 1; i < keptTaus.Count; i++)
        {
            var t = keptTaus[i];
            if (t < rangeMin) rangeMin = t;
            if (t > rangeMax) rangeMax = t;
        }

        return CumulantOutcome.Ok(new CumulantResult
        {
            FirstCumulantPerMicrosecond = gamma,
            SecondCumulantPerMicrosecondSquared = mu2,
            PolydispersityIndex = pdi,
            RSquared = rSquared,
            AppliedRangeMinMicroseconds = rangeMin,
            AppliedRangeMaxMicroseconds = rangeMax,
            PointCount = keptTaus.Count,
            Mu2WasNegative = mu2 < 0,
        });
    }

    // Solve 3×3 normal equations for the quadratic least-squares fit
    // y = a₀ + a₁·x + a₂·x² via Cramer's rule. Returns false when the
    // determinant collapses (degenerate sample, e.g. all τ identical).
    private static bool TrySolveQuadratic(
        IReadOnlyList<double> xs,
        IReadOnlyList<double> ys,
        out double a0, out double a1, out double a2)
    {
        int n = xs.Count;
        double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0;
        double sy = 0, sxy = 0, sx2y = 0;
        for (int i = 0; i < n; i++)
        {
            var x = xs[i];
            var y = ys[i];
            var x2 = x * x;
            var x3 = x2 * x;
            var x4 = x3 * x;
            sx += x;
            sx2 += x2;
            sx3 += x3;
            sx4 += x4;
            sy += y;
            sxy += x * y;
            sx2y += x2 * y;
        }

        // Coefficient matrix
        //   | n   sx   sx2 |
        //   | sx  sx2  sx3 |
        //   | sx2 sx3  sx4 |
        double Det3(double m11, double m12, double m13,
                    double m21, double m22, double m23,
                    double m31, double m32, double m33)
            => m11 * (m22 * m33 - m23 * m32)
             - m12 * (m21 * m33 - m23 * m31)
             + m13 * (m21 * m32 - m22 * m31);

        var det = Det3(n, sx, sx2,
                       sx, sx2, sx3,
                       sx2, sx3, sx4);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-30)
        {
            a0 = a1 = a2 = double.NaN;
            return false;
        }

        var det0 = Det3(sy, sx, sx2,
                        sxy, sx2, sx3,
                        sx2y, sx3, sx4);
        var det1 = Det3(n, sy, sx2,
                        sx, sxy, sx3,
                        sx2, sx2y, sx4);
        var det2 = Det3(n, sx, sy,
                        sx, sx2, sxy,
                        sx2, sx3, sx2y);

        a0 = det0 / det;
        a1 = det1 / det;
        a2 = det2 / det;
        return double.IsFinite(a0) && double.IsFinite(a1) && double.IsFinite(a2);
    }
}
