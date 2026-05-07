namespace LabPlot.DLS.Avalonia;

/// <summary>
/// Distribution kind plotted by the DLS app. The first three are the
/// classical Zetasizer particle-size distributions; <see cref="Correlation"/>
/// is the intensity autocorrelation function g₂-1 vs delay time (μs).
/// It reads from <c>DlsDataset.Correlation</c> rather than the three
/// distributions, but is treated as a fourth mode of the same
/// DistributionTypeComboBox so overlay / run-switch / per-sheet styling
/// all work uniformly.
/// </summary>
internal enum DistributionMode
{
    Number,
    Intensity,
    Volume,
    Correlation,
}
