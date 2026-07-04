using LabPlot.Core;

namespace NMRAnalyzer.Core;

/// <summary>
/// Marker interface for NMR readers (JEOL .jdf today, JCAMP-DX if that is
/// added later). Reuses <see cref="IDataReader{TDataset}"/> so the contract
/// stays in sync with the other LabPlot apps' reader interfaces.
/// </summary>
public interface INmrDataReader : IDataReader<NmrDataset>
{
}
