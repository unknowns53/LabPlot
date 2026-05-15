namespace SpectrumAnalyzer.Core;

/// <summary>
/// One row fed into <see cref="CalibrationFitter.Fit"/> — a sample's
/// concentration (in mol/L), the absorbance / area read off its dataset,
/// and an exclusion flag so outliers can be kept in the table without
/// influencing the regression.
/// </summary>
public sealed record CalibrationFitInput
{
    public required string DatasetKey { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Concentration converted to mol/L. <c>null</c> when the user
    /// hasn't typed a value yet, or the user-typed value couldn't be
    /// converted (e.g. mass-based unit without a molar mass).
    /// </summary>
    public required double? ConcentrationMolar { get; init; }

    /// <summary>
    /// Signal read off the dataset (absorbance for single-wavelength mode,
    /// baseline-subtracted area for integration mode). <see cref="double.NaN"/>
    /// is acceptable — the row will appear in the result with HasSignal =
    /// false but won't influence the fit.
    /// </summary>
    public required double Signal { get; init; }

    public bool IsExcluded { get; init; }
}

/// <summary>
/// Linear-regression fitter for Beer-Lambert calibration curves.
/// Supports both forced-origin (y = m·x, strict A = εcl) and
/// intercept-allowed (y = m·x + b) forms. ε is computed from the slope and
/// the path length and returned alongside the regression statistics.
/// </summary>
public static class CalibrationFitter
{
    public static CalibrationResult Fit(
        IReadOnlyList<CalibrationFitInput> inputs,
        CalibrationFitMode fitMode,
        CalibrationQuantificationMode quantificationMode,
        double pathLengthCm)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // Build the working set of points that actually feed the fit.
        // Rows missing a concentration / signal stay in the result table
        // for inspection but don't change the regression.
        var fitX = new List<double>(inputs.Count);
        var fitY = new List<double>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input.IsExcluded)
            {
                continue;
            }

            // Reject negative concentrations from the regression: physically
            // impossible (a calibration standard cannot have negative c) and
            // they corrupt the fit if the UI accepts a typed-in "-0.5".
            if (input.ConcentrationMolar is not double c
                || !double.IsFinite(c)
                || c < 0
                || !double.IsFinite(input.Signal))
            {
                continue;
            }

            fitX.Add(c);
            fitY.Add(input.Signal);
        }

        if (fitX.Count < 2)
        {
            return CalibrationResult.Empty(
                quantificationMode,
                fitMode,
                pathLengthCm,
                BuildPointsForEmptyFit(inputs));
        }

        var (slope, intercept, ok) = ComputeFit(fitX, fitY, fitMode);
        if (!ok)
        {
            return CalibrationResult.Empty(
                quantificationMode,
                fitMode,
                pathLengthCm,
                BuildPointsForEmptyFit(inputs));
        }

        var rSquared = ComputeRSquared(fitX, fitY, slope, intercept);

        var points = new CalibrationPoint[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var hasConcentration = input.ConcentrationMolar is double cc && double.IsFinite(cc);
            var hasSignal = hasConcentration && double.IsFinite(input.Signal);
            var concentration = input.ConcentrationMolar ?? double.NaN;
            var predicted = hasConcentration ? slope * concentration + intercept : double.NaN;
            var residual = hasSignal && double.IsFinite(predicted) ? input.Signal - predicted : double.NaN;

            points[i] = new CalibrationPoint
            {
                DatasetKey = input.DatasetKey,
                DisplayName = input.DisplayName,
                ConcentrationMolar = concentration,
                Signal = input.Signal,
                Predicted = predicted,
                Residual = residual,
                IsExcluded = input.IsExcluded,
                HasSignal = hasSignal,
            };
        }

        var epsilon = pathLengthCm > 0 && double.IsFinite(pathLengthCm)
            ? slope / pathLengthCm
            : double.NaN;

        // Flag Beer-Lambert linear-range violations on absorbance fits.
        // Practical UV-Vis spectrophotometers stay linear up to ~A=2; below
        // ~-0.05 the reading is a blank / cuvette misalignment artefact.
        // Integration-area fits don't have a universal threshold so the
        // flag is only meaningful for SingleWavelength quantification.
        var anySignalOutOfRange = false;
        if (quantificationMode == CalibrationQuantificationMode.SingleWavelength)
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (input.IsExcluded) continue;
                if (input.ConcentrationMolar is not double c
                    || !double.IsFinite(c) || c < 0) continue;
                if (!double.IsFinite(input.Signal)) continue;
                if (input.Signal < -0.05 || input.Signal > 2.0)
                {
                    anySignalOutOfRange = true;
                    break;
                }
            }
        }

        return new CalibrationResult
        {
            FitMode = fitMode,
            QuantificationMode = quantificationMode,
            Slope = slope,
            Intercept = intercept,
            RSquared = rSquared,
            N = fitX.Count,
            PathLengthCm = pathLengthCm,
            EpsilonPerCmPerMolar = epsilon,
            Points = points,
            AnySignalOutOfBeerLambertRange = anySignalOutOfRange,
        };
    }

    private static (double Slope, double Intercept, bool Ok) ComputeFit(
        List<double> xs, List<double> ys, CalibrationFitMode mode)
    {
        if (mode == CalibrationFitMode.ForceOrigin)
        {
            // Least-squares slope through the origin: m = Σ(xy) / Σ(x²).
            var sumXX = 0.0;
            var sumXY = 0.0;
            for (var i = 0; i < xs.Count; i++)
            {
                sumXX += xs[i] * xs[i];
                sumXY += xs[i] * ys[i];
            }

            if (sumXX <= 0 || !double.IsFinite(sumXX))
            {
                return (double.NaN, double.NaN, false);
            }

            return (sumXY / sumXX, 0.0, true);
        }

        // y = m·x + b via the standard normal equations.
        var n = xs.Count;
        var sX = 0.0; var sY = 0.0; var sXX = 0.0; var sXY = 0.0;
        for (var i = 0; i < n; i++)
        {
            sX += xs[i];
            sY += ys[i];
            sXX += xs[i] * xs[i];
            sXY += xs[i] * ys[i];
        }

        var det = n * sXX - sX * sX;
        if (Math.Abs(det) < 1e-30 || !double.IsFinite(det))
        {
            return (double.NaN, double.NaN, false);
        }

        var slope = (n * sXY - sX * sY) / det;
        var intercept = (sY - slope * sX) / n;
        return (slope, intercept, true);
    }

    /// <summary>
    /// Coefficient of determination using the conventional definition
    /// 1 − SS_res / SS_tot with SS_tot computed against the sample mean.
    /// Note: for a forced-origin fit this can come out negative when the
    /// origin assumption is poor — that is the correct statistical
    /// behaviour rather than a bug. Returns NaN if SS_tot is zero (all y
    /// values identical).
    /// </summary>
    private static double ComputeRSquared(List<double> xs, List<double> ys, double slope, double intercept)
    {
        var mean = 0.0;
        for (var i = 0; i < ys.Count; i++)
        {
            mean += ys[i];
        }

        mean /= ys.Count;

        var ssTot = 0.0;
        var ssRes = 0.0;
        for (var i = 0; i < ys.Count; i++)
        {
            var pred = slope * xs[i] + intercept;
            var dy = ys[i] - mean;
            var dr = ys[i] - pred;
            ssTot += dy * dy;
            ssRes += dr * dr;
        }

        if (ssTot <= 0 || !double.IsFinite(ssTot))
        {
            return double.NaN;
        }

        return 1.0 - ssRes / ssTot;
    }

    /// <summary>
    /// Build the result row list when the regression couldn't run — the
    /// rows still appear in the editor (so the user can see why the fit
    /// is empty) but predicted / residual columns are NaN.
    /// </summary>
    private static IReadOnlyList<CalibrationPoint> BuildPointsForEmptyFit(IReadOnlyList<CalibrationFitInput> inputs)
    {
        var points = new CalibrationPoint[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var hasConcentration = input.ConcentrationMolar is double cc && double.IsFinite(cc);
            var concentration = input.ConcentrationMolar ?? double.NaN;
            var hasSignal = hasConcentration && double.IsFinite(input.Signal);

            points[i] = new CalibrationPoint
            {
                DatasetKey = input.DatasetKey,
                DisplayName = input.DisplayName,
                ConcentrationMolar = concentration,
                Signal = input.Signal,
                Predicted = double.NaN,
                Residual = double.NaN,
                IsExcluded = input.IsExcluded,
                HasSignal = hasSignal,
            };
        }

        return points;
    }
}
