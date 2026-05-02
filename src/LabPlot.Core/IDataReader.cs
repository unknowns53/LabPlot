namespace LabPlot.Core;

/// <summary>
/// Generic data-reader contract: take a file path, return the dataset
/// shape the caller wants. Each LabPlot app declares a marker-style
/// derived interface (e.g. <c>IGpcDataReader</c>,
/// <c>ISpectrumDataReader</c>) so app-specific readers can be swapped
/// per format (LabSolutions TXT, JASCO TXT/CSV, Zetasizer XLSX, ...)
/// without leaking the format choice to the WPF layer.
/// </summary>
public interface IDataReader<out TDataset>
{
    TDataset Read(string filePath);
}
