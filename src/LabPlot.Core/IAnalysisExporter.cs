namespace LabPlot.Core;

/// <summary>
/// Abstraction over the per-app CSV / XLSX exporters. Each app supplies its
/// own implementation that knows how to lay out the sheet schema for its
/// dataset shape; the host code passes a populated <see cref="AnalysisExport"/>
/// and a destination path through this single contract.
/// </summary>
public interface IAnalysisExporter
{
    void Export(AnalysisExport data, string filePath);
}
