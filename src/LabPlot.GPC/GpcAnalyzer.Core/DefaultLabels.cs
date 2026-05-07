namespace GpcAnalyzer.Core;

/// <summary>
/// Default plot title / axis label strings used by the GPC app when the
/// user has not typed an override into the formatting panel and the source
/// file does not carry a more specific label. Centralized here so the
/// release-time wording can be tuned in one place rather than chasing
/// literals through MainWindow / dataset records / readers.
/// </summary>
public static class DefaultLabels
{
    public const string PlaceholderTitle = "GPC chromatogram";
    public const string PlaceholderXLabel = "Time";
    public const string PlaceholderYLabel = "Signal";

    public const string ChromatogramFallbackTitle = "GPC chromatogram";

    public const string LogScaleXLabelFormat = "{0} (log scale)";

    public const string ChromatogramDatasetXLabel = "X";
    public const string ChromatogramDatasetYLabel = "Y";

    public const string MolecularWeightDatasetXLabel = "Molecular Weight [Da]";
    public const string MolecularWeightDatasetYLabel = "Signal";
}
