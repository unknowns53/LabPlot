using LabPlot.Core;

namespace GpcAnalyzer.Core;

/// <summary>
/// GPC-specific formatting config. Inherits the LabPlot-wide font / frame /
/// background / line defaults from <see cref="GraphFormattingConfigBase"/>
/// and adds the GPC-only persistence field for the calibration file path.
/// </summary>
public sealed class GraphFormattingConfig : GraphFormattingConfigBase
{
    /// <summary>
    /// Path to the calibration file the user last loaded. Persisted alongside
    /// the formatting defaults so the next session opens against the same
    /// curve unless the user picks a new one.
    /// </summary>
    public string? DefaultCalibrationFilePath { get; set; }

    public static GraphFormattingConfig CreateFactoryDefault()
    {
        return new GraphFormattingConfig();
    }

    public override void Normalize()
    {
        base.Normalize();
        DefaultCalibrationFilePath = ConfigNormalizer.NormalizeOptionalText(DefaultCalibrationFilePath);
    }
}
