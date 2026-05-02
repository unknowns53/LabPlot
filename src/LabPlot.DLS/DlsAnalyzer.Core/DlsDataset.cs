namespace DlsAnalyzer.Core;

/// <summary>
/// One Zetasizer measurement worth of data. A single xlsx sheet maps to a
/// single <see cref="DlsDataset"/>; the workbook as a whole produces an
/// <see cref="IReadOnlyList{DlsDataset}"/>.
/// </summary>
public sealed record DlsDataset
{
    public required string SheetName { get; init; }
    public string? SampleLabel { get; init; }
    public ParticleSizeDistribution? NumberDistribution { get; init; }
    public ParticleSizeDistribution? IntensityDistribution { get; init; }
    public ParticleSizeDistribution? VolumeDistribution { get; init; }
    public CorrelationFunction? Correlation { get; init; }
    public DlsDatasetMetadata Metadata { get; init; } = new();

    public bool HasAnyDistribution =>
        NumberDistribution is not null || IntensityDistribution is not null || VolumeDistribution is not null;
}

/// <summary>
/// Particle size distribution sharing a common log-spaced size axis.
/// Zetasizer typically reports three repeats per measurement, kept in
/// <see cref="Runs"/>; UI selects one through <see cref="ActiveRunIndex"/>.
/// </summary>
public sealed record ParticleSizeDistribution
{
    public required IReadOnlyList<double> SizeBinsNm { get; init; }
    public required IReadOnlyList<IReadOnlyList<double>> Runs { get; init; }
    public int ActiveRunIndex { get; init; }

    public int RunCount => Runs.Count;

    public IReadOnlyList<double> ActiveRun
    {
        get
        {
            if (Runs.Count == 0) return Array.Empty<double>();
            var idx = Math.Clamp(ActiveRunIndex, 0, Runs.Count - 1);
            return Runs[idx];
        }
    }
}

/// <summary>Intensity autocorrelation function g₂-1 vs. delay time (μs).</summary>
public sealed record CorrelationFunction
{
    public required IReadOnlyList<double> TimesMicroseconds { get; init; }
    public required IReadOnlyList<IReadOnlyList<double>> Runs { get; init; }
    public int ActiveRunIndex { get; init; }

    public int RunCount => Runs.Count;

    public IReadOnlyList<double> ActiveRun
    {
        get
        {
            if (Runs.Count == 0) return Array.Empty<double>();
            var idx = Math.Clamp(ActiveRunIndex, 0, Runs.Count - 1);
            return Runs[idx];
        }
    }
}

/// <summary>
/// User-editable measurement metadata. Filled in from the sidebar after
/// loading; the reader leaves everything null because Zetasizer xlsx
/// exports do not embed temperature / solvent reliably.
/// </summary>
public sealed record DlsDatasetMetadata
{
    public double? TemperatureCelsius { get; init; }
    public string? Solvent { get; init; }
    public double? ConcentrationMgPerMl { get; init; }
    public double? RefractiveIndex { get; init; }
    public double? ViscosityMpas { get; init; }
}
