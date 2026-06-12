using LabPlot.Core;

namespace DataViewer.Core;

/// <summary>
/// Marker interface for generic-viewer file readers, mirroring the
/// per-app reader contracts (<c>IGpcDataReader</c>, <c>IDlsDataReader</c>)
/// so readers can be swapped per format without leaking the format choice
/// into the UI layer.
/// </summary>
public interface IViewerDataReader : IDataReader<ViewerTableSet>
{
}
