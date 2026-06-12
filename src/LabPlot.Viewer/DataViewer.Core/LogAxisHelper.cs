using System.Globalization;

namespace DataViewer.Core;

/// <summary>
/// Log-scale support shared by the viewer's axes. ScottPlot 5 has no
/// built-in log scale, so the established LabPlot pattern is used: plot
/// log10-transformed data and label the axis with decade ticks via
/// <see cref="ScottPlot.TickGenerators.NumericManual"/>.
/// </summary>
public static class LogAxisHelper
{
    /// <summary>
    /// Returns a log10-transformed copy; non-positive and non-finite
    /// values become NaN (rendered as gaps).
    /// </summary>
    public static double[] ToLog10(ReadOnlySpan<double> values)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = values[i] > 0 && double.IsFinite(values[i])
                ? Math.Log10(values[i])
                : double.NaN;
        }

        return result;
    }

    /// <summary>
    /// Decade exponent range covering the given log10-space data range,
    /// always spanning at least one full decade. Non-finite or inverted
    /// inputs fall back to 10^0–10^1.
    /// </summary>
    public static (int MinExponent, int MaxExponent) GetDecadeExponentRange(double log10Min, double log10Max)
    {
        if (!double.IsFinite(log10Min) || !double.IsFinite(log10Max) || log10Max < log10Min)
        {
            (log10Min, log10Max) = (0, 1);
        }

        var minExponent = (int)Math.Floor(log10Min);
        var maxExponent = (int)Math.Ceiling(log10Max);
        if (maxExponent == minExponent)
        {
            maxExponent++;
        }

        return (minExponent, maxExponent);
    }

    /// <summary>
    /// Builds decade major ticks (labelled 0.1 / 1 / 10 / ...) with 2–9
    /// minors covering the given range in log10 space. Inputs are the
    /// min / max of the already-transformed data.
    /// </summary>
    public static ScottPlot.TickGenerators.NumericManual CreateDecadeTicks(double log10Min, double log10Max)
    {
        var (minExponent, maxExponent) = GetDecadeExponentRange(log10Min, log10Max);
        var generator = new ScottPlot.TickGenerators.NumericManual();
        for (var exponent = minExponent; exponent <= maxExponent; exponent++)
        {
            var label = exponent switch
            {
                < 0 => Math.Pow(10, exponent).ToString("0.#########", CultureInfo.InvariantCulture),
                _ => Math.Pow(10, exponent).ToString("0", CultureInfo.InvariantCulture),
            };
            generator.AddMajor(exponent, label);
            if (exponent < maxExponent)
            {
                for (var multiplier = 2; multiplier <= 9; multiplier++)
                {
                    generator.AddMinor(exponent + Math.Log10(multiplier));
                }
            }
        }

        return generator;
    }
}
