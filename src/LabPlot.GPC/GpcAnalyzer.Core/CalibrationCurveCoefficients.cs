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

    public double CalculateMolecularWeight(double retentionTime)
    {
        return Math.Pow(10, CalculateLogMolecularWeight(retentionTime));
    }
}
