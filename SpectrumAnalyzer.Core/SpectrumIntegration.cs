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

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Label)
        && double.IsFinite(XMin)
        && double.IsFinite(XMax)
        && XMin < XMax;
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
