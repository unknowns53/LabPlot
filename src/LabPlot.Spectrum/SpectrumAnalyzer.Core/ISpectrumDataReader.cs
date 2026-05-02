using LabPlot.Core;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Marker interface for spectrum readers (JASCO Spectra Manager TXT/CSV
/// today, JCAMP-DX / Shimadzu / Agilent if those are added later).
/// Reuses <see cref="IDataReader{TDataset}"/> so the contract stays in
/// sync with the other LabPlot apps' reader interfaces.
/// </summary>
public interface ISpectrumDataReader : IDataReader<SpectrumDataset>
{
}
