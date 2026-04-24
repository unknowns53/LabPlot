namespace GpcAnalyzer.Core;

public sealed class MolecularWeightStatistics
{
    public double? Mn { get; init; }

    public double? Mw { get; init; }

    public double? Pdi { get; init; }

    public MolecularWeightStatisticsSource Source { get; init; }

    public IReadOnlyList<MolecularWeightPeak> Peaks { get; init; } = Array.Empty<MolecularWeightPeak>();

    public bool HasAnyValue => Mn.HasValue || Mw.HasValue || Pdi.HasValue || Peaks.Count > 0;
}
