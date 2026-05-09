namespace LabPlot.DLS.Avalonia;

/// <summary>
/// Distribution kind plotted by the DLS app. The first three are the
/// classical Zetasizer particle-size distributions; <see cref="Correlation"/>
/// is the intensity autocorrelation function g₂-1 vs delay time (μs);
/// <see cref="TemperatureRamp"/> aggregates each loaded sheet into a
/// single (T, d_h) point and overlays a Boltzmann sigmoid fit;
/// <see cref="ConcentrationSeries"/> aggregates each loaded sheet into a
/// single (c, D) point and overlays a linear D(c) = D₀(1 + k_D·c) fit;
/// <see cref="SizeDistributionInversion"/> reconstructs the continuous
/// particle-size distribution per selected sheet via Tikhonov-regularised
/// NNLS on g₂-1(τ) (CONTIN-style).
/// All seven share the same DistributionTypeComboBox so overlay /
/// run-switch / per-sheet styling apply uniformly.
/// </summary>
internal enum DistributionMode
{
    Number,
    Intensity,
    Volume,
    Correlation,
    TemperatureRamp,
    ConcentrationSeries,
    SizeDistributionInversion,
}
