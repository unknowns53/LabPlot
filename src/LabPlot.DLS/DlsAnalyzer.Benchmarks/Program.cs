using BenchmarkDotNet.Running;

namespace DlsAnalyzer.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        // Filter by argument when running selected benchmarks, e.g.
        //   dotnet run -c Release --project src/LabPlot.DLS/DlsAnalyzer.Benchmarks -- --filter "*Read*"
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
