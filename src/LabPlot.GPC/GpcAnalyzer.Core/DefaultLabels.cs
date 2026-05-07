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
    public const string PlaceholderTitle = "GPC Chromatogram";
    public const string PlaceholderXLabel = "Time [min]";
    public const string PlaceholderYLabel = "Signal";

    public const string ChromatogramFallbackTitle = "GPC Chromatogram";

    public const string LogScaleXLabelFormat = "{0} (log scale)";

    public const string ChromatogramDatasetXLabel = "X";
    public const string ChromatogramDatasetYLabel = "Y";

    public const string MolecularWeightDatasetXLabel = "Molecular Weight [Da]";
    public const string MolecularWeightDatasetYLabel = "Signal";

    /// <summary>
    /// Rewrite table applied to axis labels read from the source file
    /// (Shimadzu LabSolutions chromatogram headers, generic CSV / TSV
    /// header rows). When the trimmed source label matches a key here,
    /// the mapped value is used in its place; otherwise the raw source
    /// label flows through unchanged. Lookup is case-insensitive.
    /// Add entries to polish vendor wording (e.g. <c>"R.Time" → ...</c>)
    /// for publication-ready figures without having to retype the
    /// override on every session.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SourceLabelOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Examples — uncomment and edit to taste:
            // ["R.Time"] = "Retention time / min",
            // ["Intensity"] = "Signal intensity",
        };

    /// <summary>
    /// Returns the override for <paramref name="sourceLabel"/> if one is
    /// registered in <see cref="SourceLabelOverrides"/>, otherwise the
    /// input verbatim. Whitespace-only / null inputs are returned as-is
    /// so callers can compose this safely with optional values.
    /// </summary>
    public static string ApplySourceOverride(string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(sourceLabel))
        {
            return sourceLabel;
        }

        return SourceLabelOverrides.TryGetValue(sourceLabel.Trim(), out var mapped)
            ? mapped
            : sourceLabel;
    }
}
