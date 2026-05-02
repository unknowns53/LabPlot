namespace GpcAnalyzer.Core;

public sealed class MolecularWeightStatistics
{
    public const int AutoSelectionTopPercentCandidateCount = 3;

    public double? Mn { get; init; }

    public double? Mw { get; init; }

    public double? Pdi { get; init; }

    public MolecularWeightStatisticsSource Source { get; init; }

    public IReadOnlyList<MolecularWeightPeak> Peaks { get; init; } = Array.Empty<MolecularWeightPeak>();

    public string? SelectedPeakId { get; init; }

    public bool HasAnyValue => Mn.HasValue || Mw.HasValue || Pdi.HasValue || Peaks.Count > 0;

    public bool IsAutoSelected => SelectedPeakId is null;

    public MolecularWeightStatistics WithSelectedPeak(string? peakId)
    {
        if (Peaks.Count == 0)
        {
            return this;
        }

        if (peakId is null)
        {
            var auto = SelectAutoRepresentativePeak(Peaks);
            if (auto is null)
            {
                return this;
            }

            return new MolecularWeightStatistics
            {
                Mn = auto.Mn,
                Mw = auto.Mw,
                Pdi = auto.Pdi,
                Source = Source,
                Peaks = Peaks,
                SelectedPeakId = null,
            };
        }

        var manual = Peaks.FirstOrDefault(peak => string.Equals(peak.PeakId, peakId, StringComparison.OrdinalIgnoreCase));
        if (manual is null)
        {
            return this;
        }

        return new MolecularWeightStatistics
        {
            Mn = manual.Mn,
            Mw = manual.Mw,
            Pdi = manual.Pdi,
            Source = Source,
            Peaks = Peaks,
            SelectedPeakId = manual.PeakId,
        };
    }

    public static MolecularWeightPeak? SelectAutoRepresentativePeak(IReadOnlyList<MolecularWeightPeak> peaks)
    {
        if (peaks.Count == 0)
        {
            return null;
        }

        return peaks
            .OrderByDescending(peak => peak.Percent ?? double.NegativeInfinity)
            .Take(AutoSelectionTopPercentCandidateCount)
            .OrderByDescending(peak => peak.Mw ?? double.NegativeInfinity)
            .ThenByDescending(peak => peak.Percent ?? double.NegativeInfinity)
            .FirstOrDefault();
    }
}
