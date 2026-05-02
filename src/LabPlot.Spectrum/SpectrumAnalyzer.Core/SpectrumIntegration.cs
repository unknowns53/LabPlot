namespace SpectrumAnalyzer.Core;

/// <summary>
/// How to subtract a baseline before integrating a spectrum region.
/// </summary>
public enum BaselineMethod
{
    /// <summary>
    /// No baseline subtraction; the raw Y values are integrated as-is.
    /// </summary>
    None = 0,

    /// <summary>
    /// A straight line through the (XMin, Y(XMin)) and (XMax, Y(XMax)) end
    /// points of the region. Y values along that line are subtracted from
    /// the data point-by-point before integration.
    /// </summary>
    Linear = 1,

    /// <summary>
    /// Lower convex hull of the data points within the region (anchored at
    /// the region endpoints). Useful when the underlying baseline is curved
    /// rather than a chord — common for IR / Raman spectra.
    /// </summary>
    ConvexHull = 2,

    /// <summary>
    /// Rubber-band: split [XMin, XMax] into N equal-width segments, take the
    /// lowest Y in each, and connect those minima with linear segments. The
    /// baseline can ride along peak edges if N is too large for the peak
    /// width — see <see cref="RubberBandHull"/> for a smoother variant.
    /// Tunable via <see cref="IntegrationRegion.RubberBandSegments"/>.
    /// </summary>
    RubberBand = 3,

    /// <summary>
    /// Polynomial of order P fitted (least squares) through the lower convex
    /// hull vertices within the region. P is set by
    /// <see cref="IntegrationRegion.PolynomialOrder"/> and capped at 5.
    /// </summary>
    Polynomial = 4,

    /// <summary>
    /// Bruker OPUS-style rubber-band: take the segment minima as in
    /// <see cref="RubberBand"/>, then keep only the lower convex hull of
    /// those minima — so the baseline never rises onto a peak even when N
    /// is larger than the peak width.
    /// </summary>
    RubberBandHull = 5,
}

/// <summary>
/// User-defined integration region: a labelled X range with a baseline choice.
/// </summary>
public sealed record IntegrationRegion
{
    public required string Label { get; init; }

    public required double XMin { get; init; }

    public required double XMax { get; init; }

    public BaselineMethod BaselineMethod { get; init; } = BaselineMethod.Linear;

    /// <summary>
    /// Number of equal-width segments used by the RubberBand baseline. Only
    /// consulted when <see cref="BaselineMethod"/> is
    /// <see cref="BaselineMethod.RubberBand"/>.
    /// </summary>
    public int RubberBandSegments { get; init; } = 16;

    /// <summary>
    /// Order of the polynomial fitted by the Polynomial baseline. Only
    /// consulted when <see cref="BaselineMethod"/> is
    /// <see cref="BaselineMethod.Polynomial"/>. Capped at 5 by the integrator.
    /// </summary>
    public int PolynomialOrder { get; init; } = 2;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Label)
        && double.IsFinite(XMin)
        && double.IsFinite(XMax)
        && XMin < XMax
        && (BaselineMethod is not (BaselineMethod.RubberBand or BaselineMethod.RubberBandHull)
            || RubberBandSegments >= 2)
        && (BaselineMethod != BaselineMethod.Polynomial
            || (PolynomialOrder >= 1 && PolynomialOrder <= 5));
}

/// <summary>
/// Result of integrating one <see cref="IntegrationRegion"/> over one
/// <see cref="SpectrumDataset"/>. <see cref="Area"/> is the
/// baseline-subtracted integral; <see cref="RawArea"/> and
/// <see cref="BaselineArea"/> are exposed so the user can sanity-check the
/// baseline choice.
/// </summary>
public sealed record IntegrationResult
{
    public required IntegrationRegion Region { get; init; }

    public required double Area { get; init; }

    public required double RawArea { get; init; }

    public required double BaselineArea { get; init; }

    public required int PointCount { get; init; }

    /// <summary>
    /// True when the integration produced a meaningful result (at least two
    /// data points fell inside the region and X / Y values were finite).
    /// </summary>
    public bool HasResult => PointCount >= 2 && double.IsFinite(Area);
}
