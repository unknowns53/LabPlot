namespace SpectrumAnalyzer.Core;

/// <summary>
/// Converts spectrum Y values between Absorbance and Transmittance for display
/// and export. The original <see cref="SpectrumDataset"/> values are never
/// mutated; conversions are computed on demand.
/// </summary>
public static class SpectrumYAxisConverter
{
    /// <summary>
    /// Converts an absorbance value to transmittance in percent.
    /// T(%) = 100 * 10^(-A).
    /// </summary>
    public static double AbsorbanceToTransmittancePercent(double absorbance)
    {
        return 100.0 * Math.Pow(10.0, -absorbance);
    }

    /// <summary>
    /// Converts a transmittance value (in percent) to absorbance.
    /// A = -log10(T / 100). Non-positive transmittance values produce NaN
    /// because the logarithm is not defined there.
    /// </summary>
    public static double TransmittancePercentToAbsorbance(double transmittancePercent)
    {
        if (!double.IsFinite(transmittancePercent) || transmittancePercent <= 0)
        {
            return double.NaN;
        }

        return -Math.Log10(transmittancePercent / 100.0);
    }

    /// <summary>
    /// Returns true if the dataset can be rendered in the requested display mode.
    /// Reflectance, temperature, and other YUNITS that are not part of the
    /// A/T pair only support <see cref="YAxisDisplayMode.Native"/>.
    /// </summary>
    public static bool CanDisplay(SpectrumDataset dataset, YAxisDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        return mode switch
        {
            YAxisDisplayMode.Native => true,
            YAxisDisplayMode.Absorbance or YAxisDisplayMode.Transmittance =>
                dataset.IsAbsorbanceY || dataset.IsTransmittanceY,
            _ => true,
        };
    }

    /// <summary>
    /// Returns Y values prepared for the requested display mode. When the
    /// dataset's YUNITS already matches the requested mode (or the mode is
    /// Native, or the dataset cannot be converted), the dataset's own
    /// <see cref="SpectrumDataset.YValues"/> array is returned without copy.
    /// </summary>
    public static double[] GetDisplayYValues(SpectrumDataset dataset, YAxisDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (mode == YAxisDisplayMode.Native || !CanDisplay(dataset, mode))
        {
            return dataset.YValues;
        }

        if (dataset.IsAbsorbanceY && mode == YAxisDisplayMode.Transmittance)
        {
            return Convert(dataset.YValues, AbsorbanceToTransmittancePercent);
        }

        if (dataset.IsTransmittanceY && mode == YAxisDisplayMode.Absorbance)
        {
            return Convert(dataset.YValues, TransmittancePercentToAbsorbance);
        }

        // Already in the requested mode (e.g. ABSORBANCE dataset asked for Absorbance).
        return dataset.YValues;
    }

    /// <summary>
    /// Returns the Y axis label that matches the requested display mode.
    /// Falls back to the dataset's native <see cref="SpectrumDataset.YLabel"/>
    /// when the conversion does not apply.
    /// </summary>
    public static string GetDisplayYLabel(SpectrumDataset dataset, YAxisDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (mode == YAxisDisplayMode.Native || !CanDisplay(dataset, mode))
        {
            return dataset.YLabel;
        }

        return mode switch
        {
            YAxisDisplayMode.Absorbance => DefaultLabels.AbsorbanceYLabel,
            YAxisDisplayMode.Transmittance => DefaultLabels.TransmittanceYLabel,
            _ => dataset.YLabel,
        };
    }

    /// <summary>
    /// Returns display points (X is unchanged, Y is converted) for export.
    /// Returns the dataset's own <see cref="SpectrumDataset.Points"/> when
    /// no conversion is needed.
    /// </summary>
    public static IReadOnlyList<SpectrumDataPoint> GetDisplayPoints(
        SpectrumDataset dataset,
        YAxisDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (mode == YAxisDisplayMode.Native || !CanDisplay(dataset, mode))
        {
            return dataset.Points;
        }

        Func<double, double>? converter = (dataset, mode) switch
        {
            ({ IsAbsorbanceY: true }, YAxisDisplayMode.Transmittance) => AbsorbanceToTransmittancePercent,
            ({ IsTransmittanceY: true }, YAxisDisplayMode.Absorbance) => TransmittancePercentToAbsorbance,
            _ => null,
        };

        if (converter is null)
        {
            return dataset.Points;
        }

        var src = dataset.Points;
        var result = new SpectrumDataPoint[src.Count];
        for (var i = 0; i < src.Count; i++)
        {
            result[i] = new SpectrumDataPoint { X = src[i].X, Y = converter(src[i].Y) };
        }

        return result;
    }

    private static double[] Convert(double[] source, Func<double, double> converter)
    {
        var result = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = converter(source[i]);
        }

        return result;
    }
}
