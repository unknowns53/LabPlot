namespace GpcAnalyzer.Core;

public sealed class MolecularWeightDataPoint
{
    public double RetentionTime { get; init; }

    public double MolecularWeight { get; init; }

    public double LogMolecularWeight { get; init; } = double.NaN;

    public double Signal { get; init; }
}
