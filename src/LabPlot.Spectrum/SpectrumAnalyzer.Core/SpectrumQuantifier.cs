namespace SpectrumAnalyzer.Core;

/// <summary>
/// Reduces a <see cref="SpectrumDataset"/> to a single scalar that can be
/// regressed against concentration in a Beer-Lambert calibration curve.
/// Two modes are supported, mirroring
/// <see cref="CalibrationQuantificationMode"/>:
/// absorbance at a fixed wavelength (linearly interpolated, internally
/// converted to Absorbance space when the dataset is recorded as
/// Transmittance) or the baseline-subtracted area of an existing
/// <see cref="IntegrationRegion"/>.
/// </summary>
public static class SpectrumQuantifier
{
    /// <summary>
    /// Returns the absorbance of <paramref name="dataset"/> at
    /// <paramref name="wavelengthNm"/> (linearly interpolated). Returns
    /// <see cref="double.NaN"/> when the dataset cannot be expressed in
    /// Absorbance (Reflectance / temperature / …), the X axis has fewer
    /// than two points, or the requested wavelength is outside the
    /// dataset's X range.
    /// </summary>
    public static double GetAbsorbanceAt(SpectrumDataset dataset, double wavelengthNm)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (!double.IsFinite(wavelengthNm))
        {
            return double.NaN;
        }

        // Refuse to interpolate "absorbance at wavelength_nm" on an X axis
        // that is not actually a wavelength scan. CanDisplay(Absorbance)
        // only guarantees the Y axis can be expressed as absorbance, so
        // IR cm⁻¹ traces or LCST temperature scans would otherwise pass
        // through unfiltered and silently mis-interpret X.
        if (!dataset.IsWavelengthScan)
        {
            return double.NaN;
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
        {
            return double.NaN;
        }

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        return InterpolateY(xs, ys, wavelengthNm) ?? double.NaN;
    }

    /// <summary>
    /// Returns the baseline-subtracted area of <paramref name="region"/>
    /// over <paramref name="dataset"/>. <see cref="double.NaN"/> when the
    /// integrator can't produce a meaningful result (region outside the
    /// dataset, conversion to Absorbance impossible, fewer than two points
    /// in range, …).
    /// </summary>
    public static double GetIntegrationArea(SpectrumDataset dataset, IntegrationRegion region)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(region);

        var result = SpectrumIntegrator.Integrate(dataset, region);
        return result.HasResult ? result.Area : double.NaN;
    }

    /// <summary>
    /// Configuration-driven quantification entry point. Picks the right
    /// reduction (single-wavelength A or integration area) based on
    /// <paramref name="config"/> and looks up the requested integration
    /// region in <paramref name="availableRegions"/> by label. Returns
    /// <see cref="double.NaN"/> when the configuration is incomplete or the
    /// dataset can't satisfy it.
    /// </summary>
    public static double Quantify(
        SpectrumDataset dataset,
        CalibrationCurveConfig config,
        IReadOnlyList<IntegrationRegion> availableRegions)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(availableRegions);

        return config.Mode switch
        {
            CalibrationQuantificationMode.SingleWavelength
                => GetAbsorbanceAt(dataset, config.WavelengthNm),
            CalibrationQuantificationMode.IntegrationArea
                => ResolveRegion(config.IntegrationRegionLabel, availableRegions) is { } region
                    ? GetIntegrationArea(dataset, region)
                    : double.NaN,
            _ => double.NaN,
        };
    }

    /// <summary>
    /// Y-axis label suitable for the calibration plot, picked so the user
    /// can tell at a glance whether the curve is plotted against
    /// absorbance (single-wavelength mode) or area (integration mode).
    /// </summary>
    public static string GetSignalLabel(CalibrationCurveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Mode switch
        {
            CalibrationQuantificationMode.SingleWavelength =>
                $"Absorbance @ {config.WavelengthNm:0.###} nm",
            CalibrationQuantificationMode.IntegrationArea =>
                string.IsNullOrWhiteSpace(config.IntegrationRegionLabel)
                    ? "Integrated area"
                    : $"Area: {config.IntegrationRegionLabel}",
            _ => "Signal",
        };
    }

    private static IntegrationRegion? ResolveRegion(
        string? label,
        IReadOnlyList<IntegrationRegion> availableRegions)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        for (var i = 0; i < availableRegions.Count; i++)
        {
            if (string.Equals(availableRegions[i].Label, label, StringComparison.Ordinal))
            {
                return availableRegions[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Linear interpolation of Y at the given X using the two bracketing
    /// dataset points. Mirrors the behaviour of the integrator's private
    /// helper — duplicated here to keep both modules independent.
    /// </summary>
    private static double? InterpolateY(double[] xs, double[] ys, double x)
    {
        if (xs.Length < 2 || x < xs[0] || x > xs[^1])
        {
            return null;
        }

        var lo = 0;
        var hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        if (xs[lo] == x)
        {
            return ys[lo];
        }

        if (xs[hi] == x)
        {
            return ys[hi];
        }

        var t = (x - xs[lo]) / (xs[hi] - xs[lo]);
        return ys[lo] + t * (ys[hi] - ys[lo]);
    }
}
