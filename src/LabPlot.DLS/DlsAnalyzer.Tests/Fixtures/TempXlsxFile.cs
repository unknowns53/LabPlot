namespace DlsAnalyzer.Tests.Fixtures;

internal sealed class TempXlsxFile : IDisposable
{
    public string Path { get; }

    public TempXlsxFile()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
