namespace GpcAnalyzer.Core;

public interface IGpcDataReader
{
    GpcDataset Read(string filePath);
}
