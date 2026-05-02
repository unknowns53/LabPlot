using LabPlot.Core;

namespace DlsAnalyzer.Core;

/// <summary>
/// Marker interface for DLS workbook readers (Zetasizer xlsx today,
/// future formats added per file). The contract returns an ordered list
/// because a single xlsx file usually carries a day's worth of sheets,
/// each becoming one sidebar entry in the WPF app.
/// </summary>
public interface IDlsDataReader : IDataReader<IReadOnlyList<DlsDataset>>
{
}
