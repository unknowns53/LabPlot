namespace GpcAnalyzer.Core;

public sealed class CalibrationCurve
{
    public required string Solvent { get; init; }

    public required string Detector { get; init; }

    public required CalibrationCurveCoefficients Coefficients { get; init; }

    public double CalculateLogMolecularWeight(double retentionTime)
    {
        return Coefficients.CalculateLogMolecularWeight(retentionTime);
    }

    public double CalculateMolecularWeight(double retentionTime)
    {
        return Coefficients.CalculateMolecularWeight(retentionTime);
    }
}
