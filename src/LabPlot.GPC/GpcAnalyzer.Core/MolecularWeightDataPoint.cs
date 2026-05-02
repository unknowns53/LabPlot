namespace GpcAnalyzer.Core;

public readonly record struct MolecularWeightDataPoint
{
    public MolecularWeightDataPoint()
    {
        LogMolecularWeight = double.NaN;
    }

    public double RetentionTime { get; init; }

    public double MolecularWeight { get; init; }

    public double LogMolecularWeight { get; init; }

    public double Signal { get; init; }
}
