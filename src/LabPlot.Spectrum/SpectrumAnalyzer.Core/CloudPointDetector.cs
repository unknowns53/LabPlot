namespace SpectrumAnalyzer.Core;

/// <summary>
/// Method used to estimate the cloud-point temperature (Tc) of a polymer
/// solution from a transmittance vs temperature curve.
/// </summary>
public enum CloudPointMethod
{
    /// <summary>
    /// Midpoint method: Tc is the temperature at which transmittance crosses
    /// a user-defined threshold (default 50 % of the curve's vertical range).
    /// Linear interpolation between the bracketing samples.
    /// </summary>
    Midpoint = 0,

    /// <summary>
    /// First-derivative peak method: Tc is the temperature at which the
    /// magnitude of dT/dTemp is largest. Computed with a centred difference
    /// after smoothing with a small moving average.
    /// </summary>
    FirstDerivativePeak = 1,

    /// <summary>
    /// Second-derivative extremum (onset) method: returns the temperature at
    /// which |d²T/dTemp²| is largest, restricted to the side of the
    /// inflection point that corresponds to the *start* of the original
    /// sweep. For a sigmoid this picks the curvature peak adjacent to the
    /// pre-transition baseline rather than the inflection itself, giving an
    /// estimate of the transition onset.
    /// </summary>
    SecondDerivativeExtremum = 2,

    /// <summary>
    /// Boltzmann sigmoid fit: minimises the sum of squared residuals between
    /// the transmittance curve and the four-parameter Boltzmann function
    /// T(temp) = T_low + (T_high - T_low) / (1 + exp((temp - Tc) / k))
    /// using the Levenberg-Marquardt algorithm. Returns Tc directly, plus
    /// auxiliary parameters (T_low, T_high, k, R²) that are exposed via the
    /// matching nullable fields on <see cref="CloudPointResult"/>.
    /// </summary>
    SigmoidFit = 3,
}

/// <summary>
/// Configuration for <see cref="CloudPointDetector"/>. Defaults are tuned for
/// PNIPAM-style LCST sweeps in transmittance (%).
/// </summary>
public sealed record CloudPointDetectionConfig
{
    public CloudPointMethod Method { get; init; } = CloudPointMethod.Midpoint;

    /// <summary>
    /// Threshold for the midpoint method in percent. 50 % matches the
    /// classical "T₅₀" definition; users sometimes prefer 80 % for sharper
    /// transitions.
    /// </summary>
    public double TransmittanceThresholdPercent { get; init; } = 50.0;

    /// <summary>
    /// Window size (number of points) for the moving average applied to T
    /// before computing the first derivative. A value &lt;= 1 disables
    /// smoothing.
    /// </summary>
    public int SmoothingWindow { get; init; } = 3;
}

/// <summary>
/// Outcome of a single cloud-point detection on a temperature scan.
/// </summary>
public sealed record CloudPointResult
{
    public required CloudPointMethod Method { get; init; }

    public required double TemperatureCelsius { get; init; }

    /// <summary>
    /// The transmittance (%) at the detected temperature. For the midpoint
    /// method this equals the configured threshold; for the derivative method
    /// it's the interpolated value at the steepest point.
    /// </summary>
    public required double TransmittancePercentAtTc { get; init; }

    /// <summary>
    /// Original direction of the underlying scan, recovered from the file
    /// header. Hysteresis pairing keys off this value.
    /// </summary>
    public required ScanDirection Direction { get; init; }

    /// <summary>
    /// Lower asymptote of the fitted Boltzmann sigmoid (transmittance %).
    /// Populated only by <see cref="CloudPointMethod.SigmoidFit"/>; null for
    /// the other methods.
    /// </summary>
    public double? TLowPercent { get; init; }

    /// <summary>
    /// Upper asymptote of the fitted Boltzmann sigmoid (transmittance %).
    /// Populated only by <see cref="CloudPointMethod.SigmoidFit"/>; null for
    /// the other methods.
    /// </summary>
    public double? THighPercent { get; init; }

    /// <summary>
    /// Slope parameter k of the fitted Boltzmann sigmoid in °C. Smaller |k|
    /// means a sharper transition; the sign matches the original scan
    /// direction (positive for heating, negative for cooling). Populated
    /// only by <see cref="CloudPointMethod.SigmoidFit"/>.
    /// </summary>
    public double? KSlopeCelsius { get; init; }

    /// <summary>
    /// Coefficient of determination of the fit, in [0, 1]. 1.0 is a perfect
    /// fit. Populated only by <see cref="CloudPointMethod.SigmoidFit"/>.
    /// </summary>
    public double? RSquared { get; init; }

    /// <summary>
    /// Transmittance values predicted by the fitted Boltzmann sigmoid,
    /// sampled on the dataset's ascending X grid (one Y per dataset point,
    /// in transmittance %). Populated only by
    /// <see cref="CloudPointMethod.SigmoidFit"/>; null otherwise.
    /// </summary>
    public IReadOnlyList<double>? FittedCurve { get; init; }

    /// <summary>
    /// True when the dataset's transmittance curve actually contains a usable
    /// transition around the threshold (or a sufficiently sharp slope for the
    /// derivative method).
    /// </summary>
    public bool HasResult => double.IsFinite(TemperatureCelsius);

    public static CloudPointResult Empty(CloudPointMethod method, ScanDirection direction) => new()
    {
        Method = method,
        TemperatureCelsius = double.NaN,
        TransmittancePercentAtTc = double.NaN,
        Direction = direction,
    };
}

/// <summary>
/// Estimates the cloud-point temperature (Tc) from a transmittance vs
/// temperature dataset.
/// </summary>
/// <remarks>
/// Operates in transmittance space (T %). When the source dataset is recorded
/// in absorbance the values are converted on the fly via
/// <see cref="SpectrumYAxisConverter"/>. Datasets that are not temperature
/// scans, or whose Y units are neither A nor T, return an empty result.
/// </remarks>
public static class CloudPointDetector
{
    public static CloudPointResult Detect(SpectrumDataset dataset, CloudPointDetectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);

        var direction = dataset.OriginalScanDirection;

        if (!dataset.IsTemperatureScan)
        {
            return CloudPointResult.Empty(config.Method, direction);
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Transmittance))
        {
            return CloudPointResult.Empty(config.Method, direction);
        }

        var xs = dataset.XValues;
        var ts = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Transmittance);
        if (xs.Length < 3)
        {
            return CloudPointResult.Empty(config.Method, direction);
        }

        return config.Method switch
        {
            CloudPointMethod.Midpoint => DetectByMidpoint(xs, ts, config, direction),
            CloudPointMethod.FirstDerivativePeak => DetectByDerivative(xs, ts, config, direction),
            CloudPointMethod.SecondDerivativeExtremum => DetectBySecondDerivativeExtremum(xs, ts, config, direction),
            CloudPointMethod.SigmoidFit => DetectBySigmoidFit(xs, ts, config, direction),
            _ => CloudPointResult.Empty(config.Method, direction),
        };
    }

    private static CloudPointResult DetectByMidpoint(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        var threshold = config.TransmittanceThresholdPercent;
        if (!double.IsFinite(threshold))
        {
            return CloudPointResult.Empty(CloudPointMethod.Midpoint, direction);
        }

        // Find the first interval [i, i+1] in which the curve crosses the
        // threshold. The dataset's points are sorted ascending in X (set up
        // by the reader), so we scan in that order regardless of which
        // direction the original sweep was acquired.
        for (var i = 0; i < ts.Length - 1; i++)
        {
            var y0 = ts[i];
            var y1 = ts[i + 1];
            if (!double.IsFinite(y0) || !double.IsFinite(y1)) continue;

            var crosses = (y0 - threshold) * (y1 - threshold) <= 0
                          && Math.Abs(y0 - y1) > double.Epsilon;
            if (!crosses) continue;

            var t = (threshold - y0) / (y1 - y0);
            var tc = xs[i] + t * (xs[i + 1] - xs[i]);
            return new CloudPointResult
            {
                Method = CloudPointMethod.Midpoint,
                TemperatureCelsius = tc,
                TransmittancePercentAtTc = threshold,
                Direction = direction,
            };
        }

        return CloudPointResult.Empty(CloudPointMethod.Midpoint, direction);
    }

    private static CloudPointResult DetectByDerivative(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        var smoothed = MovingAverage(ts, Math.Max(1, config.SmoothingWindow));
        var bestIndex = FindFirstDerivativePeakIndex(xs, smoothed);

        if (bestIndex < 0)
        {
            return CloudPointResult.Empty(CloudPointMethod.FirstDerivativePeak, direction);
        }

        return new CloudPointResult
        {
            Method = CloudPointMethod.FirstDerivativePeak,
            TemperatureCelsius = xs[bestIndex],
            TransmittancePercentAtTc = smoothed[bestIndex],
            Direction = direction,
        };
    }

    private static CloudPointResult DetectBySecondDerivativeExtremum(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        // Find the curvature peak (|d²T/dTemp²| max) on the side of the
        // inflection that corresponds to the original sweep's *baseline*
        // — i.e. the start of the experiment. Result reads as a transition
        // onset rather than the inflection itself.
        var smoothed = MovingAverage(ts, Math.Max(1, config.SmoothingWindow));
        var inflectionIndex = FindFirstDerivativePeakIndex(xs, smoothed);
        if (inflectionIndex < 1 || inflectionIndex >= smoothed.Length - 1)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        // Sorted X is always ascending after the reader. The "baseline side"
        // of the sweep depends on whether the original scan was heating
        // (started at low T → indices 1..inflection) or cooling (started at
        // high T → indices inflection..end). When the direction is unknown
        // we search both sides and pick the larger magnitude.
        //
        // The first/last `radius` points of `smoothed` see one-sided averages,
        // which injects a spurious curvature spike at the very edge even on
        // strictly linear data. Trim those points from the search so a true
        // sigmoid is required to produce a non-zero result.
        var radius = Math.Max(1, config.SmoothingWindow) / 2;
        var lowerBound = radius + 1;
        var upperBound = smoothed.Length - 2 - radius;
        if (lowerBound > upperBound)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        var (searchStart, searchEnd) = direction switch
        {
            ScanDirection.Heating => (lowerBound, Math.Min(inflectionIndex, upperBound)),
            ScanDirection.Cooling => (Math.Max(inflectionIndex, lowerBound), upperBound),
            _ => (lowerBound, upperBound),
        };

        if (searchStart > searchEnd)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        var bestIndex = -1;
        var bestAbsCurvature = 0.0;
        for (var i = searchStart; i <= searchEnd; i++)
        {
            if (i < 1 || i >= smoothed.Length - 1) continue;
            if (!double.IsFinite(smoothed[i - 1])
                || !double.IsFinite(smoothed[i])
                || !double.IsFinite(smoothed[i + 1]))
            {
                continue;
            }

            var dxLeft = xs[i] - xs[i - 1];
            var dxRight = xs[i + 1] - xs[i];
            if (dxLeft <= 0 || dxRight <= 0) continue;

            // Centred non-uniform second difference.
            var slopeLeft = (smoothed[i] - smoothed[i - 1]) / dxLeft;
            var slopeRight = (smoothed[i + 1] - smoothed[i]) / dxRight;
            var curvature = 2.0 * (slopeRight - slopeLeft) / (dxLeft + dxRight);

            var absCurvature = Math.Abs(curvature);
            if (absCurvature > bestAbsCurvature)
            {
                bestAbsCurvature = absCurvature;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || bestAbsCurvature <= 0)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        return new CloudPointResult
        {
            Method = CloudPointMethod.SecondDerivativeExtremum,
            TemperatureCelsius = xs[bestIndex],
            TransmittancePercentAtTc = smoothed[bestIndex],
            Direction = direction,
        };
    }

    private static CloudPointResult DetectBySigmoidFit(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        // Drop NaN samples up-front so the fit only sees finite residuals.
        // We still need the centred-difference inflection / endpoint averages
        // for initial guesses, so build cleaned arrays first.
        var xClean = new List<double>(xs.Length);
        var yClean = new List<double>(xs.Length);
        for (var i = 0; i < xs.Length; i++)
        {
            if (double.IsFinite(xs[i]) && double.IsFinite(ts[i]))
            {
                xClean.Add(xs[i]);
                yClean.Add(ts[i]);
            }
        }

        if (xClean.Count < 5)
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        var xArr = xClean.ToArray();
        var yArr = yClean.ToArray();

        // Initial guesses, derived from the dataset's geometry. The Boltzmann
        // f(x) = T_low + (T_high − T_low) / (1 + e^((x−Tc)/k)) is parametrised
        // so that:
        //   k > 0 ⇒ f goes T_high → T_low as x grows (DESCENDING — PNIPAM
        //          heating, where transmittance drops past Tc),
        //   k < 0 ⇒ f goes T_low → T_high as x grows (ASCENDING — UCST,
        //          cooling-side thermo-responsive systems, …).
        // We read the plateaus off the first/last few samples and assign T_low
        // / T_high to the appropriate end before picking the sign of k.
        //   Tc = X at the inflection picked by the first-derivative-peak
        //     helper on the smoothed transmittance.
        //   |k| ≈ span / 8 — a tenth of the sweep is a good order-of-magnitude
        //     guess for the transition width on PNIPAM-like systems.
        var edgePoints = Math.Max(2, Math.Min(xArr.Length / 5, 5));
        var leftPlateau = Mean(yArr, 0, edgePoints);
        var rightPlateau = Mean(yArr, yArr.Length - edgePoints, edgePoints);

        var smoothed = MovingAverage(yArr, Math.Max(1, config.SmoothingWindow));
        var inflectionIndex = FindFirstDerivativePeakIndex(xArr, smoothed);
        var tcInit = inflectionIndex >= 0 ? xArr[inflectionIndex] : (xArr[0] + xArr[^1]) / 2.0;

        var span = xArr[^1] - xArr[0];
        if (span <= 0)
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        double tLowInit, tHighInit, kInit;
        if (leftPlateau >= rightPlateau)
        {
            // Descending: x small → T_high (left), x large → T_low (right).
            tLowInit = rightPlateau;
            tHighInit = leftPlateau;
            kInit = span / 8.0;
        }
        else
        {
            // Ascending: x small → T_low (left), x large → T_high (right).
            tLowInit = leftPlateau;
            tHighInit = rightPlateau;
            kInit = -span / 8.0;
        }

        var p = new[] { tLowInit, tHighInit, tcInit, kInit };

        if (!LevenbergMarquardt(xArr, yArr, p))
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        var tLow = p[0];
        var tHigh = p[1];
        var tc = p[2];
        var k = p[3];

        if (!double.IsFinite(tLow) || !double.IsFinite(tHigh)
            || !double.IsFinite(tc) || !double.IsFinite(k)
            || Math.Abs(k) < 1e-9)
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        // Reject runaway plateaus and clearly-degenerate fits. Asymptotes that
        // lie entirely outside [-50 %, 150 %] mean the optimiser walked off
        // into noise; the original curve is bounded to roughly [0, 100] %.
        if (tLow < -50 || tLow > 150 || tHigh < -50 || tHigh > 150)
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        if (Math.Abs(tHigh - tLow) < 1.0)
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        // Tc must land inside the measured range — extrapolated centres are
        // not physically meaningful for a Tc estimate.
        if (tc < xArr[0] - 0.1 * span || tc > xArr[^1] + 0.1 * span)
        {
            return CloudPointResult.Empty(CloudPointMethod.SigmoidFit, direction);
        }

        // Predicted Y at every original (sorted-ascending) sample, including
        // any NaN-bearing rows: matches the dataset's grid 1:1 so the UI can
        // overlay it without re-aligning.
        var fittedFull = new double[xs.Length];
        for (var i = 0; i < xs.Length; i++)
        {
            fittedFull[i] = double.IsFinite(xs[i]) ? Boltzmann(xs[i], tLow, tHigh, tc, k) : double.NaN;
        }

        var rSquared = ComputeRSquared(xArr, yArr, tLow, tHigh, tc, k);
        var transmittanceAtTc = (tLow + tHigh) / 2.0;

        return new CloudPointResult
        {
            Method = CloudPointMethod.SigmoidFit,
            TemperatureCelsius = tc,
            TransmittancePercentAtTc = transmittanceAtTc,
            Direction = direction,
            TLowPercent = tLow,
            THighPercent = tHigh,
            KSlopeCelsius = k,
            RSquared = rSquared,
            FittedCurve = fittedFull,
        };
    }

    /// <summary>
    /// Boltzmann sigmoid: T(x) = T_low + (T_high - T_low) / (1 + e^u)
    /// where u = (x - Tc) / k. Computed via the numerically-stable logistic
    /// to avoid overflow for |u| ≫ 1.
    /// </summary>
    private static double Boltzmann(double x, double tLow, double tHigh, double tc, double k)
    {
        var u = (x - tc) / k;
        var sigma = Logistic(-u);
        return tLow + (tHigh - tLow) * sigma;
    }

    /// <summary>
    /// 1 / (1 + e^(-z)), evaluated in the form that does not overflow for
    /// large |z| (branches on sign of z).
    /// </summary>
    private static double Logistic(double z)
    {
        if (z >= 0)
        {
            var e = Math.Exp(-z);
            return 1.0 / (1.0 + e);
        }
        else
        {
            var e = Math.Exp(z);
            return e / (1.0 + e);
        }
    }

    /// <summary>
    /// Levenberg-Marquardt fit of the four-parameter Boltzmann sigmoid. Updates
    /// <paramref name="p"/> in place; returns false when the iteration cannot
    /// make progress (singular normal matrix, blow-up, …).
    /// </summary>
    /// <remarks>
    /// Standard textbook LMA: at each iteration form J ᵀ J and J ᵀ r, scale
    /// the diagonal by (1 + λ), solve a 4×4 linear system for the step Δp,
    /// accept the step if χ² decreases (λ ÷= 10) and reject otherwise
    /// (λ ×= 10). Stopping criterion is a relative parameter step below 1e-8
    /// or 100 outer iterations.
    /// </remarks>
    private static bool LevenbergMarquardt(double[] x, double[] y, double[] p)
    {
        const int maxIterations = 100;
        const double tolerance = 1e-8;
        var lambda = 1e-3;
        var chi2 = ChiSquared(x, y, p);
        if (!double.IsFinite(chi2))
        {
            return false;
        }

        var jtj = new double[4, 4];
        var jtr = new double[4];
        var grad = new double[4];

        for (var iter = 0; iter < maxIterations; iter++)
        {
            // Reset accumulators for this iteration's J ᵀ J / J ᵀ r build.
            for (var i = 0; i < 4; i++)
            {
                jtr[i] = 0.0;
                for (var j = 0; j < 4; j++)
                {
                    jtj[i, j] = 0.0;
                }
            }

            for (var n = 0; n < x.Length; n++)
            {
                var predicted = ComputeJacobian(x[n], p, grad);
                var residual = y[n] - predicted;
                for (var i = 0; i < 4; i++)
                {
                    jtr[i] += grad[i] * residual;
                    for (var j = 0; j < 4; j++)
                    {
                        jtj[i, j] += grad[i] * grad[j];
                    }
                }
            }

            // Damp the diagonal: (J ᵀ J + λ · diag(J ᵀ J)) Δp = J ᵀ r.
            var aug = new double[4, 4];
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++)
                {
                    aug[i, j] = jtj[i, j];
                }
                aug[i, i] *= 1.0 + lambda;
            }

            var rhs = new double[4];
            Array.Copy(jtr, rhs, 4);
            if (!Solve4x4(aug, rhs, out var delta))
            {
                lambda *= 10.0;
                if (lambda > 1e10) return false;
                continue;
            }

            var trial = new[] { p[0] + delta[0], p[1] + delta[1], p[2] + delta[2], p[3] + delta[3] };
            if (Math.Abs(trial[3]) < 1e-12) trial[3] = Math.Sign(p[3]) * 1e-12;
            var trialChi2 = ChiSquared(x, y, trial);

            if (double.IsFinite(trialChi2) && trialChi2 < chi2)
            {
                // Accept the step: tighten damping and adopt the trial params.
                var pNorm = ParamNorm(p);
                var dNorm = ParamNorm(delta);
                Array.Copy(trial, p, 4);
                chi2 = trialChi2;
                lambda = Math.Max(lambda / 10.0, 1e-12);
                if (pNorm > 0 && dNorm / pNorm < tolerance)
                {
                    return true;
                }
            }
            else
            {
                // Reject: increase damping and retry from the same point.
                lambda *= 10.0;
                if (lambda > 1e10)
                {
                    return false;
                }
            }
        }

        // Iteration cap reached — accept whatever we have if the residual is
        // still finite. The plateau / Tc / k validity gates in the caller
        // catch obviously bad fits.
        return double.IsFinite(chi2);
    }

    private static double ParamNorm(double[] p)
    {
        var sum = 0.0;
        for (var i = 0; i < p.Length; i++) sum += p[i] * p[i];
        return Math.Sqrt(sum);
    }

    private static double ChiSquared(double[] x, double[] y, double[] p)
    {
        var sum = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            var r = y[i] - Boltzmann(x[i], p[0], p[1], p[2], p[3]);
            sum += r * r;
        }
        return sum;
    }

    /// <summary>
    /// Fills <paramref name="grad"/> with ∂f/∂p for the Boltzmann sigmoid and
    /// returns the predicted value f(x; p) so the caller can form r = y − f.
    /// </summary>
    private static double ComputeJacobian(double xv, double[] p, double[] grad)
    {
        var tLow = p[0];
        var tHigh = p[1];
        var tc = p[2];
        var k = p[3];

        var u = (xv - tc) / k;
        var sigma = Logistic(-u);     // = 1 / (1 + e^u)
        var oneMinus = 1.0 - sigma;   // = e^u / (1 + e^u)
        var span = tHigh - tLow;
        var dsigmaDu = -sigma * oneMinus;                    // d sigma / d u

        grad[0] = oneMinus;                                  // ∂f/∂T_low
        grad[1] = sigma;                                     // ∂f/∂T_high
        grad[2] = span * dsigmaDu * (-1.0 / k);              // ∂f/∂Tc
        grad[3] = span * dsigmaDu * (-(xv - tc) / (k * k));  // ∂f/∂k

        return tLow + span * sigma;
    }

    private static bool Solve4x4(double[,] a, double[] b, out double[] x)
    {
        x = new double[4];
        // Gaussian elimination with partial pivoting on the 4×4 augmented
        // system [a | b]. Pivot magnitudes below 1e-14 mean the local Hessian
        // is effectively singular — back off and let the caller bump λ.
        for (var i = 0; i < 4; i++)
        {
            var pivotRow = i;
            var pivotMag = Math.Abs(a[i, i]);
            for (var k = i + 1; k < 4; k++)
            {
                var mag = Math.Abs(a[k, i]);
                if (mag > pivotMag)
                {
                    pivotMag = mag;
                    pivotRow = k;
                }
            }

            if (pivotMag < 1e-14)
            {
                return false;
            }

            if (pivotRow != i)
            {
                for (var j = 0; j < 4; j++)
                {
                    (a[i, j], a[pivotRow, j]) = (a[pivotRow, j], a[i, j]);
                }
                (b[i], b[pivotRow]) = (b[pivotRow], b[i]);
            }

            var pivot = a[i, i];
            for (var k = i + 1; k < 4; k++)
            {
                var factor = a[k, i] / pivot;
                for (var j = i; j < 4; j++)
                {
                    a[k, j] -= factor * a[i, j];
                }
                b[k] -= factor * b[i];
            }
        }

        for (var i = 3; i >= 0; i--)
        {
            var sum = b[i];
            for (var j = i + 1; j < 4; j++)
            {
                sum -= a[i, j] * x[j];
            }
            x[i] = sum / a[i, i];
        }

        return true;
    }

    private static double ComputeRSquared(double[] x, double[] y, double tLow, double tHigh, double tc, double k)
    {
        var meanY = 0.0;
        for (var i = 0; i < y.Length; i++) meanY += y[i];
        meanY /= y.Length;

        var ssTot = 0.0;
        var ssRes = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            var pred = Boltzmann(x[i], tLow, tHigh, tc, k);
            var r = y[i] - pred;
            ssRes += r * r;
            var d = y[i] - meanY;
            ssTot += d * d;
        }

        if (ssTot <= 0) return 0.0;
        var rsq = 1.0 - ssRes / ssTot;
        if (!double.IsFinite(rsq)) return 0.0;
        return Math.Clamp(rsq, 0.0, 1.0);
    }

    private static double Mean(double[] arr, int start, int count)
    {
        var sum = 0.0;
        var n = 0;
        for (var i = start; i < start + count && i < arr.Length; i++)
        {
            if (double.IsFinite(arr[i]))
            {
                sum += arr[i];
                n++;
            }
        }
        return n > 0 ? sum / n : 0.0;
    }

    private static int FindFirstDerivativePeakIndex(double[] xs, double[] smoothed)
    {
        var bestIndex = -1;
        var bestSlope = 0.0;
        for (var i = 1; i < smoothed.Length - 1; i++)
        {
            var dx = xs[i + 1] - xs[i - 1];
            if (dx <= 0 || !double.IsFinite(smoothed[i + 1]) || !double.IsFinite(smoothed[i - 1]))
            {
                continue;
            }

            var slope = (smoothed[i + 1] - smoothed[i - 1]) / dx;
            if (Math.Abs(slope) > Math.Abs(bestSlope))
            {
                bestSlope = slope;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static double[] MovingAverage(double[] source, int window)
    {
        if (window <= 1 || source.Length == 0)
        {
            return source;
        }

        var radius = window / 2;
        var result = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var sum = 0.0;
            var count = 0;
            for (var j = Math.Max(0, i - radius); j <= Math.Min(source.Length - 1, i + radius); j++)
            {
                if (!double.IsFinite(source[j])) continue;
                sum += source[j];
                count++;
            }

            result[i] = count > 0 ? sum / count : double.NaN;
        }

        return result;
    }
}

/// <summary>
/// Pairs heating/cooling cloud-point results to expose the hysteresis width
/// ΔT = Tc(cooling) − Tc(heating). Returns NaN when the pair is incomplete.
/// </summary>
public static class HysteresisAnalyzer
{
    public static double ComputeHysteresis(CloudPointResult? heating, CloudPointResult? cooling)
    {
        if (heating is null || cooling is null) return double.NaN;
        if (!heating.HasResult || !cooling.HasResult) return double.NaN;
        return cooling.TemperatureCelsius - heating.TemperatureCelsius;
    }
}
