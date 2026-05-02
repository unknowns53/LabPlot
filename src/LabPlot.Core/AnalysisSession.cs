namespace LabPlot.Core;

/// <summary>
/// Shared base for the per-app analysis session containers. Holds the
/// metadata (version / timestamp / generator name) and the cross-app
/// presentation state (overlay flag, active dataset index, axis labels)
/// that every LabPlot app saves into its session file.
/// </summary>
/// <remarks>
/// App-specific payload (datasets, axes, formatting, calibration, ...)
/// lives on the subclass so JSON round-trips preserve the concrete type
/// without requiring polymorphic JSON contracts.
/// <see cref="AnalysisSessionStore{TSession}"/> deserialises straight into
/// the subclass and then calls <see cref="EnsureDefaults"/> so subclasses
/// can rebuild any concrete <c>Datasets</c> / <c>Axes</c> / <c>Formatting</c>
/// containers that came back null from a partial JSON payload.
/// </remarks>
public abstract class AnalysisSession
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    public string GeneratorName { get; set; } = "LabPlot";

    public bool Overlay { get; set; }

    public int ActiveDatasetIndex { get; set; } = -1;

    public AnalysisSessionLabels Labels { get; set; } = new();

    /// <summary>
    /// Restores any subclass-specific containers that may have come back
    /// null from a partial JSON payload, and normalises the formatting
    /// config if the subclass holds one. Called by
    /// <see cref="AnalysisSessionStore{TSession}"/> right after
    /// deserialisation.
    /// </summary>
    public abstract void EnsureDefaults();
}
