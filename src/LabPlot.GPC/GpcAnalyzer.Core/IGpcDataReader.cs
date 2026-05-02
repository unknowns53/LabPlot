using LabPlot.Core;

namespace GpcAnalyzer.Core;

/// <summary>
/// Marker interface for GPC chromatogram readers (LabSolutions TXT today,
/// future formats added per file). Reuses
/// <see cref="IDataReader{TDataset}"/> so the contract stays in sync with
/// the other LabPlot apps' reader interfaces.
/// </summary>
public interface IGpcDataReader : IDataReader<GpcDataset>
{
}
