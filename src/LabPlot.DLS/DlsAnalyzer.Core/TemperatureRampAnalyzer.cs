namespace DlsAnalyzer.Core;

/// <summary>
/// One (T, d_h) pair feeding the temperature-ramp Boltzmann fit. T is
/// the measured sample temperature in °C; d_h is the hydrodynamic
/// diameter (nm) recovered by <see cref="CumulantAnalyzer"/> +
/// <see cref="StokesEinstein"/> on the same dataset.
/// </summary>
public sealed record TemperatureRampPoint(double TemperatureCelsius, double DiameterNm);

/// <summary>
/// Result of a successful Boltzmann sigmoid fit on a temperature
/// ramp. The model is
///     d_h(T) = d_low + (d_high - d_low) / (1 + exp(-(T - T_c) / w))
/// with all four parameters free. Sign of <paramref name="w"/> is
/// solved for, so an inverted ramp (d_h decreasing with T) returns a
/// negative width without further user action.
/// </summary>
public sealed record TemperatureRampResult
{
    public required double TransitionTemperatureCelsius { get; init; }
    public required double TransitionWidthCelsius { get; init; }
    public required double LowPlateauNm { get; init; }
    public required double HighPlateauNm { get; init; }
    public required double RSquared { get; init; }
    public required int IterationCount { get; init; }
    public required int PointCount { get; init; }
}

/// <summary>
/// Outcome wrapper letting the UI render a structured failure message
/// (insufficient points, fit divergence, etc.) instead of swallowing
/// the error.
/// </summary>
public sealed record TemperatureRampOutcome
{
    public required bool Success { get; init; }
    public TemperatureRampResult? Result { get; init; }
    public string? FailureReason { get; init; }

    public static TemperatureRampOutcome Ok(TemperatureRampResult result)
        => new() { Success = true, Result = result };

    public static TemperatureRampOutcome Fail(string reason)
        => new() { Success = false, FailureReason = reason };
}

/// <summary>
/// Levenberg–Marquardt fit of a 4-parameter Boltzmann sigmoid to a
/// temperature ramp.
/// </summary>
/// <remarks>
/// Convergence strategy: parameters are normalised so the diameter-
/// scale (low / high plateau) and temperature-scale (T_c / w)
/// derivatives sit in comparable ranges; the damping term λ on the
/// (Jᵀ J + λ·diag(JᵀJ)) normal equations is multiplied by 10 on a
/// rejected step and divided by 10 on an accepted one (classical
/// Marquardt update). Termination is on relative cost change below
/// <see cref="ConvergenceTolerance"/> for two consecutive accepted
/// steps, or on <see cref="MaxIterations"/> exhausted.
/// </remarks>
public static class TemperatureRampAnalyzer
{
    public const int MinimumPointCount = 4;
    public const int MaxIterations = 200;
    public const double ConvergenceTolerance = 1e-9;
    public const double MinimumTransitionWidth = 1e-3;
    /// <summary>
    /// Minimum spread of the input temperature axis (°C). Anything tighter
    /// leaves the Boltzmann sigmoid massively under-determined.
    /// </summary>
    public const double MinimumTemperatureSpanCelsius = 1.0;

    public static TemperatureRampOutcome Analyze(IReadOnlyList<TemperatureRampPoint>? points)
    {
        if (points is null)
            return TemperatureRampOutcome.Fail("温度ランプデータがありません");

        var filtered = new List<TemperatureRampPoint>(points.Count);
        foreach (var p in points)
        {
            if (!double.IsFinite(p.TemperatureCelsius)) continue;
            if (!double.IsFinite(p.DiameterNm) || p.DiameterNm <= 0) continue;
            filtered.Add(p);
        }
        filtered.Sort((a, b) => a.TemperatureCelsius.CompareTo(b.TemperatureCelsius));

        if (filtered.Count < MinimumPointCount)
            return TemperatureRampOutcome.Fail(
                $"有効な温度点が不足しています（{filtered.Count}/{MinimumPointCount} 点）");

        // Reject perfectly-flat ramps (e.g. all five sheets sitting at
        // 25 °C): the fit becomes ill-conditioned and T_c is meaningless.
        var tMin = filtered[0].TemperatureCelsius;
        var tMax = filtered[^1].TemperatureCelsius;
        if (tMax - tMin < MinimumTemperatureSpanCelsius)
            return TemperatureRampOutcome.Fail(
                $"温度範囲が狭すぎます（少なくとも {MinimumTemperatureSpanCelsius} °C のスパンが必要）");

        var ts = new double[filtered.Count];
        var ys = new double[filtered.Count];
        for (int i = 0; i < filtered.Count; i++)
        {
            ts[i] = filtered[i].TemperatureCelsius;
            ys[i] = filtered[i].DiameterNm;
        }

        // Initial parameter guesses derived from the data:
        //   d_low / d_high — ramp endpoints
        //   T_c            — temperature whose d_h is closest to the midpoint
        //   w              — a tenth of the temperature span (sign decided by
        //                     whether d_h is rising or falling with T)
        var dLow0 = ys[0];
        var dHigh0 = ys[^1];
        var midValue = 0.5 * (dLow0 + dHigh0);
        var tcInit = ts[ts.Length / 2];
        var minDiff = double.MaxValue;
        for (int i = 0; i < ys.Length; i++)
        {
            var diff = Math.Abs(ys[i] - midValue);
            if (diff < minDiff)
            {
                minDiff = diff;
                tcInit = ts[i];
            }
        }
        var span = tMax - tMin;
        // Initial width always positive — the plateau parameters carry
        // the sign of the ramp via (d_low > d_high) for cooling-driven
        // transitions. Letting wInit go negative duplicates the curve
        // symmetry f(d_low, d_high, w) ≡ f(d_high, d_low, -w) and
        // confuses the meaning of returned LowPlateauNm / HighPlateauNm.
        var wInit = span / 10.0;

        var parameters = new[] { dLow0, dHigh0, tcInit, wInit };
        var residuals = new double[ts.Length];
        var jacobian = new double[ts.Length, 4];

        ComputeResiduals(ts, ys, parameters, residuals);
        var cost = SumSquares(residuals);
        var lambda = 1e-3;

        int acceptedIterations = 0;
        int convergedSteps = 0;
        bool dampingExhausted = false;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            ComputeJacobian(ts, parameters, jacobian);

            // Build normal equations:  (Jᵀ J + λ diag(Jᵀ J)) δ = Jᵀ r
            var jtj = ComputeJtJ(jacobian);
            var jtr = ComputeJtR(jacobian, residuals);

            // Marquardt damping: scale the diagonal by (1 + λ).
            for (int k = 0; k < 4; k++) jtj[k, k] *= (1.0 + lambda);

            if (!Solve4x4(jtj, jtr, out var delta))
            {
                lambda *= 10.0;
                if (lambda > 1e12) { dampingExhausted = true; break; }
                continue;
            }

            var trial = new[]
            {
                parameters[0] + delta[0],
                parameters[1] + delta[1],
                parameters[2] + delta[2],
                parameters[3] + delta[3],
            };

            // Keep |w| above the floor so the sigmoid does not collapse
            // into a step function during iteration.
            if (Math.Abs(trial[3]) < MinimumTransitionWidth)
                trial[3] = trial[3] >= 0 ? MinimumTransitionWidth : -MinimumTransitionWidth;

            var trialResiduals = new double[ts.Length];
            ComputeResiduals(ts, ys, trial, trialResiduals);
            var trialCost = SumSquares(trialResiduals);

            if (trialCost < cost)
            {
                var rel = (cost - trialCost) / Math.Max(cost, 1e-30);
                cost = trialCost;
                Array.Copy(trial, parameters, 4);
                Array.Copy(trialResiduals, residuals, ts.Length);
                lambda = Math.Max(lambda / 10.0, 1e-12);
                acceptedIterations++;
                convergedSteps = rel < ConvergenceTolerance ? convergedSteps + 1 : 0;
                if (convergedSteps >= 2) break;
            }
            else
            {
                lambda *= 10.0;
                if (lambda > 1e12) { dampingExhausted = true; break; }
            }
        }

        // Bailed out via damping runaway with no usable progress: the
        // initial guess gave up before a meaningful descent. Distinct
        // from acceptedIterations==0 because here we did try multiple
        // steps but none reduced the residual.
        if (dampingExhausted && acceptedIterations < 2)
            return TemperatureRampOutcome.Fail("LM が damping exhaustion で打ち切られました");

        if (!parameters.All(double.IsFinite))
            return TemperatureRampOutcome.Fail("fit が発散しました");

        var dLow = parameters[0];
        var dHigh = parameters[1];
        var tc = parameters[2];
        var w = parameters[3];

        // R² in the linear-domain fit — use the data variance as the
        // null model and the residual cost as the unexplained variance.
        double meanY = 0;
        for (int i = 0; i < ys.Length; i++) meanY += ys[i];
        meanY /= ys.Length;
        double ssTot = 0;
        for (int i = 0; i < ys.Length; i++)
        {
            var dy = ys[i] - meanY;
            ssTot += dy * dy;
        }

        // Reject obviously degenerate fits before computing R²:
        // (a) flat diameter series — any T_c / w is consistent with the
        //     data so the recovered transition is meaningless.
        // (b) fits where the LM loop never accepted a step — the
        //     "parameters" remain the data-derived initial guesses.
        // (c) recovered plateau amplitude below the data variability,
        //     which leaves T_c and w under-determined.
        if (ssTot <= 0)
            return TemperatureRampOutcome.Fail("d_h が平坦で Boltzmann fit が非同定です");
        if (acceptedIterations == 0)
            return TemperatureRampOutcome.Fail("LM が収束しませんでした");

        var dataStdDev = Math.Sqrt(ssTot / Math.Max(ys.Length - 1, 1));
        var amplitude = Math.Abs(dHigh - dLow);
        if (amplitude < 0.25 * dataStdDev)
            return TemperatureRampOutcome.Fail("d の変化量がノイズスケール以下で Boltzmann fit が非同定です");

        // T_c outside the measured span (tolerated up to one span beyond
        // each end) means the sigmoid was pinned to a near-asymptote and
        // the recovered T_c is essentially an extrapolation artefact.
        if (!double.IsFinite(tc) || tc < tMin - span || tc > tMax + span)
            return TemperatureRampOutcome.Fail("T_c が測定範囲から離れすぎています");

        var rSquared = 1.0 - cost / ssTot;
        if (!double.IsFinite(rSquared) || rSquared < 0)
            return TemperatureRampOutcome.Fail("fit の品質が低すぎます");

        return TemperatureRampOutcome.Ok(new TemperatureRampResult
        {
            TransitionTemperatureCelsius = tc,
            TransitionWidthCelsius = w,
            LowPlateauNm = dLow,
            HighPlateauNm = dHigh,
            RSquared = rSquared,
            IterationCount = acceptedIterations,
            PointCount = filtered.Count,
        });
    }

    /// <summary>Predicted d_h at temperature t using the supplied parameters.</summary>
    public static double Predict(double temperatureCelsius, TemperatureRampResult result)
    {
        var p = new[]
        {
            result.LowPlateauNm,
            result.HighPlateauNm,
            result.TransitionTemperatureCelsius,
            result.TransitionWidthCelsius,
        };
        return PredictWithParameters(temperatureCelsius, p);
    }

    private static double PredictWithParameters(double t, double[] p)
    {
        var w = Math.Abs(p[3]) < MinimumTransitionWidth
            ? (p[3] >= 0 ? MinimumTransitionWidth : -MinimumTransitionWidth)
            : p[3];
        var s = Sigmoid((t - p[2]) / w);
        return p[0] + (p[1] - p[0]) * s;
    }

    private static void ComputeResiduals(double[] ts, double[] ys, double[] p, double[] residuals)
    {
        for (int i = 0; i < ts.Length; i++)
            residuals[i] = ys[i] - PredictWithParameters(ts[i], p);
    }

    private static void ComputeJacobian(double[] ts, double[] p, double[,] jacobian)
    {
        var w = Math.Abs(p[3]) < MinimumTransitionWidth
            ? (p[3] >= 0 ? MinimumTransitionWidth : -MinimumTransitionWidth)
            : p[3];
        var amp = p[1] - p[0];
        for (int i = 0; i < ts.Length; i++)
        {
            var u = (ts[i] - p[2]) / w;
            var s = Sigmoid(u);
            var ds = s * (1.0 - s);
            // Sign convention: residual = y - f, so df/dp enters as the
            // negation. We store df/dp here and let ComputeJtR keep the
            // overall sign consistent with the Jᵀ r right-hand side.
            jacobian[i, 0] = 1.0 - s;                  // ∂f/∂d_low
            jacobian[i, 1] = s;                         // ∂f/∂d_high
            jacobian[i, 2] = -amp * ds / w;             // ∂f/∂T_c
            jacobian[i, 3] = -amp * ds * (ts[i] - p[2]) / (w * w); // ∂f/∂w
        }
    }

    private static double[,] ComputeJtJ(double[,] j)
    {
        var n = j.GetLength(0);
        var jtj = new double[4, 4];
        for (int a = 0; a < 4; a++)
            for (int b = 0; b <= a; b++)
            {
                double sum = 0;
                for (int i = 0; i < n; i++) sum += j[i, a] * j[i, b];
                jtj[a, b] = sum;
                jtj[b, a] = sum;
            }
        return jtj;
    }

    private static double[] ComputeJtR(double[,] j, double[] residuals)
    {
        var n = j.GetLength(0);
        var jtr = new double[4];
        for (int a = 0; a < 4; a++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += j[i, a] * residuals[i];
            jtr[a] = sum;
        }
        return jtr;
    }

    private static double SumSquares(double[] r)
    {
        double sum = 0;
        for (int i = 0; i < r.Length; i++) sum += r[i] * r[i];
        return sum;
    }

    private static double Sigmoid(double x)
    {
        // Numerically stable across the full real line.
        if (x >= 0)
        {
            var e = Math.Exp(-x);
            return 1.0 / (1.0 + e);
        }
        else
        {
            var e = Math.Exp(x);
            return e / (1.0 + e);
        }
    }

    private static bool Solve4x4(double[,] a, double[] b, out double[] x)
    {
        // Gaussian elimination with partial pivoting on a 4x4 system.
        var m = new double[4, 5];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++) m[i, j] = a[i, j];
            m[i, 4] = b[i];
        }
        for (int k = 0; k < 4; k++)
        {
            int pivot = k;
            double pivotValue = Math.Abs(m[k, k]);
            for (int i = k + 1; i < 4; i++)
            {
                if (Math.Abs(m[i, k]) > pivotValue)
                {
                    pivot = i;
                    pivotValue = Math.Abs(m[i, k]);
                }
            }
            if (pivotValue < 1e-30)
            {
                x = new double[4];
                return false;
            }
            if (pivot != k)
            {
                for (int j = 0; j < 5; j++)
                {
                    (m[k, j], m[pivot, j]) = (m[pivot, j], m[k, j]);
                }
            }
            for (int i = k + 1; i < 4; i++)
            {
                var factor = m[i, k] / m[k, k];
                for (int j = k; j < 5; j++) m[i, j] -= factor * m[k, j];
            }
        }
        x = new double[4];
        for (int i = 3; i >= 0; i--)
        {
            double sum = m[i, 4];
            for (int j = i + 1; j < 4; j++) sum -= m[i, j] * x[j];
            x[i] = sum / m[i, i];
        }
        return x.All(double.IsFinite);
    }
}
