namespace GpcAnalyzer.Core;

public interface IAnalysisExporter
{
    void Export(AnalysisExport data, string filePath);
}
