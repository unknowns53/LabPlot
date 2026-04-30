namespace SpectrumAnalyzer.Core;

/// <summary>
/// How the viewer should render and export the Y axis of a spectrum.
/// </summary>
public enum YAxisDisplayMode
{
    /// <summary>
    /// Use the YUNITS recorded in the source file as-is.
    /// </summary>
    Native = 0,

    /// <summary>
    /// Force the viewer to display Absorbance, converting from Transmittance
    /// when needed (A = -log10(T / 100)).
    /// </summary>
    Absorbance = 1,

    /// <summary>
    /// Force the viewer to display Transmittance (%), converting from
    /// Absorbance when needed (T = 100 * 10^(-A)).
    /// </summary>
    Transmittance = 2,
}
