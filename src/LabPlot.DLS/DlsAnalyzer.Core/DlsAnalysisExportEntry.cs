using LabPlot.Core;

namespace DlsAnalyzer.Core;

/// <summary>
/// DLS-specific analysis export row. Inherits the shared display /
/// source / axis-label fields from <see cref="AnalysisExportEntry"/>
/// and adds the active-mode plot points, the optional cumulant fit
/// outcome, the Stokes–Einstein hydrodynamic diameter, and a snapshot
/// of the per-sheet measurement metadata so a single CSV / XLSX
/// captures everything the user sees in the sidebar.
/// </summary>
/// <remarks>
/// X / Y are pre-evaluated for the displayed mode: particle-size
/// distributions hand back nm × percent, the correlation mode hands
/// back μs × g₂-1. <see cref="DistributionMode"/> is a string for
/// JSON-friendliness; the four valid values are Number / Intensity /
/// Volume / Correlation.
/// </remarks>
public sealed class DlsAnalysisExportEntry : AnalysisExportEntry
{
    public string DistributionMode { get; init; } = "Number";

    public IReadOnlyList<double> Xs { get; init; } = Array.Empty<double>();

    public IReadOnlyList<double> Ys { get; init; } = Array.Empty<double>();

    public CumulantResult? Cumulant { get; init; }

    public double? HydrodynamicDiameterNm { get; init; }

    public double? TemperatureCelsius { get; init; }

    public string? Solvent { get; init; }

    public double? ConcentrationMgPerMl { get; init; }

    public double? RefractiveIndex { get; init; }

    public double? ViscosityMpas { get; init; }

    public double? WavelengthNm { get; init; }

    public double? ScatteringAngleDegrees { get; init; }
}
