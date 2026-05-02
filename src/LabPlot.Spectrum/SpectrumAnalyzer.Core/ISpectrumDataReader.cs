namespace SpectrumAnalyzer.Core;

public interface ISpectrumDataReader
{
    SpectrumDataset Read(string filePath);
}
