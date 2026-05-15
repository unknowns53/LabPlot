namespace DlsAnalyzer.Core;

/// <summary>
/// One (c, D) pair feeding the concentration-series fit. <paramref
/// name="ConcentrationMgPerMl"/> is the polymer concentration in
/// mg/mL (the unit the LabPlot UI uses for the metadata field), and
/// <paramref name="DiffusionCoefficientM2PerSecond"/> is the diffusion
/// coefficient recovered by <see cref="CumulantAnalyzer"/> +
/// <see cref="StokesEinstein"/> on the same dataset.
/// </summary>
public sealed record ConcentrationSeriesPoint(
    double ConcentrationMgPerMl,
    double DiffusionCoefficientM2PerSecond);

/// <summary>
/// Result of a successful concentration-series fit. The model is
///     D(c) = D₀ · (1 + k_D · c)
/// where c is internally converted to g/mL so that <see cref="KDmlPerGram"/>
/// is reported in the conventional mL/g unit. <see cref="HydrodynamicDiameterAtZeroConcentrationNm"/>
/// is the "true" R_h reconstructed from D₀ via Stokes–Einstein at the
/// supplied reference temperature and viscosity.
/// </summary>
public sealed record ConcentrationSeriesResult
{
    public required double D0M2PerSecond { get; init; }
    public required double KDmlPerGram { get; init; }
    public required double HydrodynamicDiameterAtZeroConcentrationNm { get; init; }
    public required double RSquared { get; init; }
    public required int PointCount { get; init; }
    /// <summary>1σ standard error of the OLS intercept (D₀).</summary>
    public required double D0StandardErrorM2PerSecond { get; init; }
    /// <summary>1σ standard error of the OLS slope (D₀·k_D).</summary>
    public required double SlopeStandardError { get; init; }
    /// <summary>
    /// 1σ standard error on k_D propagated from the slope and intercept
    /// uncertainties. Useful for "kD = a ± b mL/g" reporting.
    /// </summary>
    public required double KDStandardErrorMlPerGram { get; init; }
    /// <summary>Reference temperature used for the d_h(c=0) conversion (°C).</summary>
    public required double ReferenceTemperatureCelsius { get; init; }
    /// <summary>Reference viscosity used for the d_h(c=0) conversion (mPa·s).</summary>
    public required double ReferenceViscosityMpas { get; init; }
}

/// <summary>
/// Outcome wrapper letting the UI render a structured failure message
/// (insufficient points, flat concentration range, etc.) instead of
/// swallowing the error.
/// </summary>
public sealed record ConcentrationSeriesOutcome
{
    public required bool Success { get; init; }
    public ConcentrationSeriesResult? Result { get; init; }
    public string? FailureReason { get; init; }

    public static ConcentrationSeriesOutcome Ok(ConcentrationSeriesResult result)
        => new() { Success = true, Result = result };

    public static ConcentrationSeriesOutcome Fail(string reason)
        => new() { Success = false, FailureReason = reason };
}

/// <summary>
/// Ordinary least-squares fit of D vs c followed by extraction of the
/// diffusion virial coefficient k_D and the infinite-dilution
/// hydrodynamic diameter.
/// </summary>
/// <remarks>
/// Physics:
///   D(c) = D₀ · (1 + k_D · c)
///   d_h(c=0) = k_B · T / (3π · η · D₀)
/// The slope of D vs c (with c in g/mL) divided by the intercept
/// gives k_D in mL/g. The intercept is fed back through Stokes–Einstein
/// at a single reference temperature/viscosity to give the "true"
/// hydrodynamic diameter free from inter-particle interaction effects.
/// Standard errors come from the textbook closed-form OLS expressions
/// (Bevington / Bruns).
/// </remarks>
public static class ConcentrationSeriesAnalyzer
{
    public const int MinimumPointCount = 3;
    public const double MinimumConcentrationSpreadMgPerMl = 1e-3;

    public static ConcentrationSeriesOutcome Analyze(
        IReadOnlyList<ConcentrationSeriesPoint>? points,
        double referenceTemperatureCelsius,
        double referenceViscosityMpas)
    {
        if (points is null)
            return ConcentrationSeriesOutcome.Fail("濃度シリーズデータがありません");

        if (!double.IsFinite(referenceTemperatureCelsius) || referenceTemperatureCelsius + 273.15 <= 0)
            return ConcentrationSeriesOutcome.Fail("参照温度が不正です");
        if (!double.IsFinite(referenceViscosityMpas) || referenceViscosityMpas <= 0)
            return ConcentrationSeriesOutcome.Fail("参照粘度が不正です");

        var filtered = new List<ConcentrationSeriesPoint>(points.Count);
        foreach (var p in points)
        {
            if (!double.IsFinite(p.ConcentrationMgPerMl) || p.ConcentrationMgPerMl < 0) continue;
            if (!double.IsFinite(p.DiffusionCoefficientM2PerSecond) || p.DiffusionCoefficientM2PerSecond <= 0) continue;
            filtered.Add(p);
        }
        filtered.Sort((a, b) => a.ConcentrationMgPerMl.CompareTo(b.ConcentrationMgPerMl));

        if (filtered.Count < MinimumPointCount)
            return ConcentrationSeriesOutcome.Fail(
                $"有効な濃度点が不足しています（{filtered.Count}/{MinimumPointCount} 点）");

        var cMin = filtered[0].ConcentrationMgPerMl;
        var cMax = filtered[^1].ConcentrationMgPerMl;
        if (cMax - cMin < MinimumConcentrationSpreadMgPerMl)
            return ConcentrationSeriesOutcome.Fail("濃度範囲が狭すぎます（少なくとも 0.001 mg/mL のスパンが必要）");

        // Convert c to g/mL inside the regression so that the slope
        // divided by the intercept gives k_D directly in mL/g.
        int n = filtered.Count;
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = filtered[i].ConcentrationMgPerMl * 1e-3;
            ys[i] = filtered[i].DiffusionCoefficientM2PerSecond;
        }

        double sumX = 0, sumY = 0;
        for (int i = 0; i < n; i++) { sumX += xs[i]; sumY += ys[i]; }
        var meanX = sumX / n;
        var meanY = sumY / n;

        double sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            var dx = xs[i] - meanX;
            sxx += dx * dx;
            sxy += dx * (ys[i] - meanY);
        }

        if (sxx <= 0)
            return ConcentrationSeriesOutcome.Fail("濃度の分散が 0 です");

        var slope = sxy / sxx;
        var intercept = meanY - slope * meanX;

        if (!double.IsFinite(intercept) || intercept <= 0)
            return ConcentrationSeriesOutcome.Fail(
                "切片 D₀ が非物理的です（外挿で負またはゼロになりました）");

        // Goodness of fit: standard SS_tot / SS_res construction.
        double ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            var predicted = intercept + slope * xs[i];
            var dy = ys[i] - meanY;
            ssTot += dy * dy;
            var r = ys[i] - predicted;
            ssRes += r * r;
        }
        var rSquared = ssTot > 0 ? 1.0 - ssRes / ssTot : 1.0;

        // Standard errors from the residual variance (Bevington 6.13–6.15).
        // Two parameters fit, so the unbiased residual variance uses n - 2.
        double slopeSE = 0, interceptSE = 0, sigma2 = 0;
        if (n > 2)
        {
            sigma2 = ssRes / (n - 2);
            slopeSE = Math.Sqrt(sigma2 / sxx);
            interceptSE = Math.Sqrt(sigma2 * (1.0 / n + meanX * meanX / sxx));
        }

        var kD = slope / intercept;
        // Propagate (slope, intercept) uncertainties to k_D = slope/intercept
        // using the full OLS covariance — slope and intercept are correlated
        // when meanX ≠ 0:
        //     Cov(a, b) = -meanX · σ² / sxx
        //     Var(k_D) = Var(b)/a² + b²·Var(a)/a⁴ - 2·b·Cov(a, b)/a³
        // Intercept is guaranteed positive above (D₀ ≤ 0 returns Fail), and
        // slope can legitimately be zero (D independent of c) — the σ_b/a
        // term still contributes a non-degenerate uncertainty in that case.
        var covAB = -meanX * sigma2 / sxx;
        var kDVar = (slopeSE * slopeSE) / (intercept * intercept)
                    + (slope * slope) * (interceptSE * interceptSE) / Math.Pow(intercept, 4)
                    - (2.0 * slope * covAB) / Math.Pow(intercept, 3);
        var kDSE = kDVar > 0 ? Math.Sqrt(kDVar) : 0.0;

        // Stokes–Einstein at the reference (T, η) gives the d_h that
        // matches D₀, i.e. the diameter free from inter-particle drag.
        var tKelvin = referenceTemperatureCelsius + 273.15;
        var etaPa = referenceViscosityMpas * 1e-3;
        var diameterMeter = StokesEinstein.BoltzmannJoulePerKelvin * tKelvin
                            / (3.0 * Math.PI * etaPa * intercept);
        var diameterNm = diameterMeter * 1e9;
        if (!double.IsFinite(diameterNm) || diameterNm <= 0)
            return ConcentrationSeriesOutcome.Fail(
                "d_h(c→0) が非物理的です（参照温度・粘度を確認してください）");

        return ConcentrationSeriesOutcome.Ok(new ConcentrationSeriesResult
        {
            D0M2PerSecond = intercept,
            KDmlPerGram = kD,
            HydrodynamicDiameterAtZeroConcentrationNm = diameterNm,
            RSquared = rSquared,
            PointCount = n,
            D0StandardErrorM2PerSecond = interceptSE,
            SlopeStandardError = slopeSE,
            KDStandardErrorMlPerGram = kDSE,
            ReferenceTemperatureCelsius = referenceTemperatureCelsius,
            ReferenceViscosityMpas = referenceViscosityMpas,
        });
    }

    /// <summary>Predicted D at concentration c (mg/mL) using the fitted parameters.</summary>
    public static double Predict(double concentrationMgPerMl, ConcentrationSeriesResult r)
        => r.D0M2PerSecond * (1.0 + r.KDmlPerGram * concentrationMgPerMl * 1e-3);
}
