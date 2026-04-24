namespace GpcAnalyzer.Core;

public sealed class MolecularWeightPeak
{
    public required string PeakId { get; init; }

    public double? Mn { get; init; }

    public double? Mw { get; init; }

    public double? Pdi { get; init; }

    public double? Percent { get; init; }

    public bool HasAnyValue => Mn.HasValue || Mw.HasValue || Pdi.HasValue || Percent.HasValue;
}
