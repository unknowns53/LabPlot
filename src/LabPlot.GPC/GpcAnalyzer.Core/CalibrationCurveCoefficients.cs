using System.Text.Json.Serialization;

namespace GpcAnalyzer.Core;

public sealed class CalibrationCurveCoefficients
{
    [JsonPropertyName("a")]
    public double A { get; init; }

    [JsonPropertyName("b")]
    public double B { get; init; }

    [JsonPropertyName("c")]
    public double C { get; init; }

    [JsonPropertyName("d")]
    public double D { get; init; }

    public double CalculateLogMolecularWeight(double retentionTime)
    {
        return (A * retentionTime * retentionTime * retentionTime)
            + (B * retentionTime * retentionTime)
            + (C * retentionTime)
            + D;
    }

    /// <summary>
    /// Computes 10^logM with an overflow guard. The cubic fit can extrapolate
    /// to extreme logM values (especially near the void volume / solvent peak
    /// of a chromatogram that includes points outside the calibration window),
    /// where Math.Pow returns Infinity and corrupts every downstream
    /// area-weighted statistic. Returning NaN here lets the caller's
    /// finite-value filters reject the point cleanly.
    /// </summary>
    public double CalculateMolecularWeight(double retentionTime)
    {
        var logM = CalculateLogMolecularWeight(retentionTime);
        if (!double.IsFinite(logM))
        {
            return double.NaN;
        }

        var molecularWeight = Math.Pow(10, logM);
        return double.IsFinite(molecularWeight) ? molecularWeight : double.NaN;
    }

    /// <summary>
    /// First derivative d(logM)/dt of the cubic. GPC calibration curves
    /// should keep the derivative sign consistent across the fitted retention
    /// window (logM monotonically decreases as t increases for size exclusion
    /// chromatography). Sign reversal across the dataset is a strong hint
    /// that some chromatogram points landed in the polynomial's extrapolation
    /// tail, where the cubic wraps around and assigns physically nonsensical
    /// MW values.
    /// </summary>
    public double CalculateLogMolecularWeightDerivative(double retentionTime)
    {
        return (3.0 * A * retentionTime * retentionTime)
            + (2.0 * B * retentionTime)
            + C;
    }
}
