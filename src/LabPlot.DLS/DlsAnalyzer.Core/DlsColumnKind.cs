namespace DlsAnalyzer.Core;

/// <summary>Column kinds emitted by Zetasizer xlsx exports (header keyword based).</summary>
public enum DlsColumnKind
{
    Unknown,
    SizeAxis,
    NumberPercent,
    IntensityPercent,
    VolumePercent,
    TimeAxis,
    CorrelationG2Minus1,
}
