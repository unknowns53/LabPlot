namespace LabPlot.DLS.Avalonia;

/// <summary>
/// Default plot title / axis label strings used by the DLS app when the
/// user has not typed an override into the formatting panel. Centralized
/// here so the release-time wording can be tuned in one place rather than
/// chasing literals through MainWindow's plot routines and switch helpers.
/// </summary>
internal static class DefaultLabels
{
    public const string ParticleSizeDistributionTitle = "Particle Size Distribution";
    public const string CorrelationFunctionTitle = "Correlation Function";

    // Suffix appended to GetModeLabel(...) for the empty-state title shown
    // when none of the selected datasets carries the requested distribution.
    // Keeps the leading space so callers can interpolate it directly.
    public const string NoDataSuffix = " データなし";

    public const string SizeXLabel = "Size [nm]";
    public const string CorrelationTimeXLabel = "Time [μs]";

    public const string NumberYLabel = "Number [%]";
    public const string IntensityYLabel = "Intensity [%]";
    public const string VolumeYLabel = "Volume [%]";
    public const string CorrelationYLabel = "g₂-1";

    public static string GetPlotTypeLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Correlation => CorrelationFunctionTitle,
        _ => ParticleSizeDistributionTitle,
    };

    public static string GetDefaultXLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Correlation => CorrelationTimeXLabel,
        _ => SizeXLabel,
    };

    public static string GetModeLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Intensity => IntensityYLabel,
        DistributionMode.Volume => VolumeYLabel,
        DistributionMode.Correlation => CorrelationYLabel,
        _ => NumberYLabel,
    };
}
