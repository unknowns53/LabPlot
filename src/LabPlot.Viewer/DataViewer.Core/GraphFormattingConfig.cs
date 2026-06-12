using LabPlot.Core;

namespace DataViewer.Core;

/// <summary>
/// Viewer-specific formatting config. The generic viewer has no extra
/// persistence fields yet — axis scale toggles live on the session's axes
/// instead — so this subclass only pins the concrete JSON round-trip type.
/// </summary>
public sealed class GraphFormattingConfig : GraphFormattingConfigBase
{
    public static GraphFormattingConfig CreateFactoryDefault()
    {
        return new GraphFormattingConfig();
    }
}
