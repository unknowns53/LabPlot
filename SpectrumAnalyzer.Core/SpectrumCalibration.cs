using System.Globalization;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// How a spectrum is reduced to a single number for the Beer-Lambert
/// calibration curve.
/// </summary>
public enum CalibrationQuantificationMode
{
    /// <summary>
    /// Absorbance at a fixed wavelength (linearly interpolated). Datasets
    /// recorded as Transmittance are internally converted via
    /// <see cref="SpectrumYAxisConverter"/>.
    /// </summary>
    SingleWavelength = 0,

    /// <summary>
    /// Baseline-subtracted area of an existing <see cref="IntegrationRegion"/>.
    /// </summary>
    IntegrationArea = 1,
}

/// <summary>
/// Linear-regression form used for the calibration curve.
/// </summary>
public enum CalibrationFitMode
{
    /// <summary>
    /// y = m·x. Strictly Beer-Lambert — assumes A = 0 at c = 0.
    /// </summary>
    ForceOrigin = 0,

    /// <summary>
    /// y = m·x + b. Allows for a baseline / blank offset on the absorbance
    /// axis.
    /// </summary>
    WithIntercept = 1,
}

/// <summary>
/// Concentration units accepted in the calibration editor. Internally all
/// values are converted to mol/L (M) so that ε comes out in
/// M⁻¹·cm⁻¹ regardless of the user-facing unit.
/// </summary>
public enum CalibrationConcentrationUnit
{
    MolPerLiter = 0,
    MillimolPerLiter = 1,
    MicromolPerLiter = 2,
    NanomolPerLiter = 3,
    MilligramPerMilliliter = 4,
    GramPerLiter = 5,
}

/// <summary>
/// One sample row in a calibration set: a dataset reference + the
/// concentration the user typed in (in the parent
/// <see cref="CalibrationCurveConfig.ConcentrationUnit"/>) + an exclusion flag.
/// </summary>
public sealed class CalibrationSample
{
    /// <summary>
    /// Stable key used to reattach the sample to a loaded dataset between
    /// sessions. Falls back to the source file path when the dataset has no
    /// title.
    /// </summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>
    /// Concentration the user typed in, expressed in the parent's
    /// <see cref="CalibrationCurveConfig.ConcentrationUnit"/>. Null means
    /// the row has no concentration assigned yet (excluded from the fit).
    /// </summary>
    public double? ConcentrationInUnit { get; set; }

    /// <summary>
    /// When true, the sample is kept in the editor for reference but
    /// excluded from the linear regression — useful for outliers.
    /// </summary>
    public bool IsExcluded { get; set; }
}

/// <summary>
/// Persisted configuration for a Beer-Lambert calibration curve. Lives on
/// <see cref="GraphFormattingConfig.Calibration"/> so it round-trips through
/// the session file alongside the integration regions.
/// </summary>
public sealed class CalibrationCurveConfig
{
    public CalibrationQuantificationMode Mode { get; set; } = CalibrationQuantificationMode.SingleWavelength;

    /// <summary>
    /// Wavelength (nm) at which the absorbance is read for
    /// <see cref="CalibrationQuantificationMode.SingleWavelength"/>.
    /// Defaults to 280 nm — a sensible baseline for protein / aromatic
    /// chromophores.
    /// </summary>
    public double WavelengthNm { get; set; } = 280.0;

    /// <summary>
    /// Label of the integration region used when the mode is
    /// <see cref="CalibrationQuantificationMode.IntegrationArea"/>. The
    /// region itself is stored in
    /// <see cref="GraphFormattingConfig.IntegrationRegions"/> — this is
    /// just a foreign key so a region rename / delete shows up here.
    /// </summary>
    public string? IntegrationRegionLabel { get; set; }

    /// <summary>
    /// Optical path length in centimetres. Beer-Lambert ε = slope / l.
    /// </summary>
    public double PathLengthCm { get; set; } = 1.0;

    public CalibrationFitMode FitMode { get; set; } = CalibrationFitMode.ForceOrigin;

    public CalibrationConcentrationUnit ConcentrationUnit { get; set; } =
        CalibrationConcentrationUnit.MicromolPerLiter;

    /// <summary>
    /// Molar mass (g/mol) used to convert mass-based concentrations
    /// (mg/mL, g/L) to mol/L. Ignored for purely molar units.
    /// </summary>
    public double? MolarMass { get; set; }

    /// <summary>
    /// One row per dataset the user has assigned a concentration to.
    /// Keyed by <see cref="CalibrationSample.DatasetKey"/>.
    /// </summary>
    public IList<CalibrationSample> Samples { get; set; } = new List<CalibrationSample>();
}

/// <summary>
/// One point on the fitted calibration curve. Returned by
/// <see cref="CalibrationFitter"/> alongside the fit parameters so the UI
/// can render residuals / mark excluded points without re-deriving them.
/// </summary>
public sealed record CalibrationPoint
{
    public required string DatasetKey { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Concentration converted to mol/L (M).</summary>
    public required double ConcentrationMolar { get; init; }

    /// <summary>
    /// Absorbance (single-wavelength mode) or baseline-subtracted area
    /// (integration mode) read off the dataset.
    /// </summary>
    public required double Signal { get; init; }

    /// <summary>Predicted signal from the fit at this concentration.</summary>
    public required double Predicted { get; init; }

    /// <summary>Signal − Predicted.</summary>
    public required double Residual { get; init; }

    public required bool IsExcluded { get; init; }

    /// <summary>
    /// True when both <see cref="ConcentrationMolar"/> and
    /// <see cref="Signal"/> are finite — failed lookups (missing dataset,
    /// out-of-range wavelength, …) leave the point in the table for
    /// inspection but with HasSignal = false.
    /// </summary>
    public required bool HasSignal { get; init; }
}

/// <summary>
/// Fit summary returned by <see cref="CalibrationFitter.Fit"/>. ε is
/// derived from the slope and the path length so callers don't need to
/// know the formula.
/// </summary>
public sealed record CalibrationResult
{
    public required CalibrationFitMode FitMode { get; init; }

    public required CalibrationQuantificationMode QuantificationMode { get; init; }

    /// <summary>Slope of the regression (signal per mol/L).</summary>
    public required double Slope { get; init; }

    /// <summary>Intercept on the signal axis. Always 0 when the fit was
    /// forced through the origin.</summary>
    public required double Intercept { get; init; }

    public required double RSquared { get; init; }

    /// <summary>Number of points actually used in the fit (after exclusion
    /// and dropping rows with missing concentration / signal).</summary>
    public required int N { get; init; }

    public required double PathLengthCm { get; init; }

    /// <summary>
    /// ε = slope / l, in M⁻¹·cm⁻¹. Only meaningful for the single-wavelength
    /// quantification mode — for area-based fits this is an "ε-equivalent"
    /// number whose unit depends on the X axis.
    /// </summary>
    public required double EpsilonPerCmPerMolar { get; init; }

    public required IReadOnlyList<CalibrationPoint> Points { get; init; }

    /// <summary>True when at least 2 points fed the regression and the slope
    /// is finite.</summary>
    public bool HasFit => N >= 2 && double.IsFinite(Slope);

    public static CalibrationResult Empty(
        CalibrationQuantificationMode quantificationMode,
        CalibrationFitMode fitMode,
        double pathLengthCm,
        IReadOnlyList<CalibrationPoint>? points = null) => new()
    {
        FitMode = fitMode,
        QuantificationMode = quantificationMode,
        Slope = double.NaN,
        Intercept = double.NaN,
        RSquared = double.NaN,
        N = 0,
        PathLengthCm = pathLengthCm,
        EpsilonPerCmPerMolar = double.NaN,
        Points = points ?? Array.Empty<CalibrationPoint>(),
    };
}

/// <summary>
/// Helpers for converting between user-facing concentration units and the
/// internal mol/L representation, plus tiny presentation helpers used by
/// both the editor UI and the export code.
/// </summary>
public static class CalibrationUnitConverter
{
    /// <summary>
    /// Convert a user-typed value to mol/L. Returns <c>null</c> when the
    /// conversion can't be done (non-finite input, mass-based unit without
    /// a molar mass, unknown enum value).
    /// </summary>
    public static double? ToMolar(
        double valueInUnit,
        CalibrationConcentrationUnit unit,
        double? molarMassGramsPerMol)
    {
        if (!double.IsFinite(valueInUnit))
        {
            return null;
        }

        return unit switch
        {
            CalibrationConcentrationUnit.MolPerLiter => valueInUnit,
            CalibrationConcentrationUnit.MillimolPerLiter => valueInUnit * 1e-3,
            CalibrationConcentrationUnit.MicromolPerLiter => valueInUnit * 1e-6,
            CalibrationConcentrationUnit.NanomolPerLiter => valueInUnit * 1e-9,
            CalibrationConcentrationUnit.MilligramPerMilliliter or CalibrationConcentrationUnit.GramPerLiter
                => molarMassGramsPerMol is { } mw && double.IsFinite(mw) && mw > 0
                    ? valueInUnit / mw
                    : null,
            _ => null,
        };
    }

    /// <summary>True for units that need an explicit molar mass to be
    /// converted to mol/L.</summary>
    public static bool RequiresMolarMass(CalibrationConcentrationUnit unit) =>
        unit is CalibrationConcentrationUnit.MilligramPerMilliliter
             or CalibrationConcentrationUnit.GramPerLiter;

    /// <summary>Display symbol used in the UI and in CSV/XLSX exports.</summary>
    public static string GetSymbol(CalibrationConcentrationUnit unit) => unit switch
    {
        CalibrationConcentrationUnit.MolPerLiter => "M",
        CalibrationConcentrationUnit.MillimolPerLiter => "mM",
        CalibrationConcentrationUnit.MicromolPerLiter => "μM",
        CalibrationConcentrationUnit.NanomolPerLiter => "nM",
        CalibrationConcentrationUnit.MilligramPerMilliliter => "mg/mL",
        CalibrationConcentrationUnit.GramPerLiter => "g/L",
        _ => string.Empty,
    };

    /// <summary>
    /// Parse a free-form unit token (the underlying enum name, the symbol
    /// returned by <see cref="GetSymbol"/>, or a few common aliases) back
    /// into the enum. Used when restoring sessions / config files.
    /// </summary>
    public static CalibrationConcentrationUnit? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var normalized = token.Trim();
        return normalized switch
        {
            "M" or "mol/L" or "MolPerLiter" => CalibrationConcentrationUnit.MolPerLiter,
            "mM" or "mmol/L" or "MillimolPerLiter" => CalibrationConcentrationUnit.MillimolPerLiter,
            "uM" or "μM" or "umol/L" or "MicromolPerLiter"
                => CalibrationConcentrationUnit.MicromolPerLiter,
            "nM" or "nmol/L" or "NanomolPerLiter" => CalibrationConcentrationUnit.NanomolPerLiter,
            "mg/mL" or "mg/ml" or "MilligramPerMilliliter"
                => CalibrationConcentrationUnit.MilligramPerMilliliter,
            "g/L" or "g/l" or "GramPerLiter" => CalibrationConcentrationUnit.GramPerLiter,
            _ => null,
        };
    }

    /// <summary>
    /// Default culture-invariant formatter used by the export and the
    /// summary text. Keeps small concentrations readable while still
    /// faithfully representing larger values.
    /// </summary>
    public static string FormatConcentration(double valueInUnit) =>
        double.IsFinite(valueInUnit)
            ? valueInUnit.ToString("G6", CultureInfo.InvariantCulture)
            : string.Empty;
}
