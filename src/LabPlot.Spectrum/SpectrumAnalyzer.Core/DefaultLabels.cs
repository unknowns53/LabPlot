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
    public const string TransmittanceYLabel = "Transmittance / %";
}
