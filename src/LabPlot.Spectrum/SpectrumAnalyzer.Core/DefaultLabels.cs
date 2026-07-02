namespace SpectrumAnalyzer.Core;

/// <summary>
/// Default plot title / axis label strings used by the Spectrum app when
/// the user has not typed an override into the formatting panel and the
/// JASCO file does not carry a more specific label. Centralized here so
/// the release-time wording can be tuned in one place rather than chasing
/// literals through MainWindow / SpectrumDataset / SpectrumYAxisConverter.
/// </summary>
public static class DefaultLabels
{
    public const string PlaceholderTitle = "Spectrum";
    public const string PlaceholderXLabel = "X";
    public const string PlaceholderYLabel = "Y";

    public const string SpectrumFallbackTitle = "Spectrum";

    public const string DatasetXLabel = "X";
    public const string DatasetYLabel = "Y";

    public const string AbsorbanceYLabel = "Absorbance";
    public const string TransmittanceYLabel = "Transmittance [%]";

    /// <summary>
    /// X axis label shown by the empty-plot preset. Matches the wording a
    /// loaded UV-Vis wavelength scan uses once <see cref="SourceLabelOverrides"/>
    /// has normalized the JCAMP-DX XUNITS string, so the placeholder axis
    /// reads the same as the real one once a file is opened.
    /// </summary>
    public const string WavelengthXLabel = "Wavelength [nm]";

    /// <summary>
    /// Rewrite table applied to axis labels derived from the source file
    /// (the JCAMP-DX XUNITS / YUNITS strings after JascoSpectrumReader's
    /// AxisLabelMapper has translated them to display form). When the
    /// trimmed label matches a key here, the mapped value is used in
    /// its place; otherwise the AxisLabelMapper output flows through
    /// unchanged. Lookup is case-insensitive. Add entries to polish
    /// instrument-derived wording (e.g. <c>"Wavelength (nm)" → ...</c>)
    /// for publication-ready figures without having to retype the
    /// override on every session.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SourceLabelOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Examples — uncomment and edit to taste:
            ["Wavelength (nm)"] = "Wavelength [nm]",
            ["Wavenumber (cm⁻¹)"] = "Wavenumber [cm⁻¹]",
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
