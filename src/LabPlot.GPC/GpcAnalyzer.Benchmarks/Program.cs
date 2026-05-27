using BenchmarkDotNet.Running;

namespace GpcAnalyzer.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        // Filter by argument when running selected benchmarks, e.g.
        //   dotnet run -c Release --project src/LabPlot.GPC/GpcAnalyzer.Benchmarks -- --filter "*Read*"
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
