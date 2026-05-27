using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Benchmarks;

/// <summary>
/// Baseline benchmarks for <see cref="JascoSpectrumReader"/>.
/// Three parameter sizes simulate realistic JASCO Spectra Manager exports:
/// 2k points (typical UV-Vis 800–200 nm at 0.3 nm step), 5k points (extended
/// UV-Vis or modest FT-IR), and 10k points (dense FT-IR mid-resolution).
///
/// The reader only exposes <c>Read(string filePath)</c>, so each iteration
/// reads from a single temp file written once in <see cref="GlobalSetup"/>;
/// disk I/O is part of the timing but the parser dominates at larger sizes.
/// </summary>
[MemoryDiagnoser]
public class JascoSpectrumReaderBenchmarks
{
    private readonly JascoSpectrumReader _reader = new();
    private string _smallPath = string.Empty;
    private string _mediumPath = string.Empty;
    private string _largePath = string.Empty;

    [Params(2_000, 5_000, 10_000)]
    public int PointCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _smallPath = WriteSyntheticJascoFile(2_000);
        _mediumPath = WriteSyntheticJascoFile(5_000);
        _largePath = WriteSyntheticJascoFile(10_000);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        TryDelete(_smallPath);
        TryDelete(_mediumPath);
        TryDelete(_largePath);
    }

    [Benchmark]
    public SpectrumDataset Read()
    {
        var path = PointCount switch
        {
            2_000 => _smallPath,
            5_000 => _mediumPath,
            _ => _largePath,
        };
        return _reader.Read(path);
    }

    private static string WriteSyntheticJascoFile(int pointCount)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"labplot-spectrum-bench-{pointCount}-{Guid.NewGuid():N}.txt");
        var content = BuildSyntheticJasco(pointCount);
        // Use Shift-JIS so the encoding-detection branch in the reader runs
        // through its real production code path.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding("shift_jis");
        File.WriteAllText(path, content, shiftJis);
        return path;
    }

    private static string BuildSyntheticJasco(int pointCount)
    {
        var sb = new StringBuilder(capacity: pointCount * 20);
        var inv = CultureInfo.InvariantCulture;

        // Mirror JASCO Spectra Manager V-750 UV-Vis export structure. All
        // identifying fields use neutral placeholders (no personal data).
        const double firstX = 800.0;
        const double lastX = 200.0;
        var deltaX = (lastX - firstX) / (pointCount - 1);

        sb.Append("TITLE\t").Append('\n');
        sb.Append("DATA TYPE\t").Append('\n');
        sb.Append("ORIGIN\tJASCO").Append('\n');
        sb.Append("OWNER\t").Append('\n');
        sb.Append("DATE\t26/05/27").Append('\n');
        sb.Append("TIME\t12:00:00").Append('\n');
        sb.Append("SPECTROMETER/DATA SYSTEM\tJASCO Corp., V-750, Rev. 1.00").Append('\n');
        sb.Append("RESOLUTION\t").Append('\n');
        sb.Append("DELTAX\t").Append(deltaX.ToString("F3", inv)).Append('\n');
        sb.Append("XUNITS\tNANOMETERS").Append('\n');
        sb.Append("YUNITS\tABSORBANCE").Append('\n');
        sb.Append("FIRSTX\t").Append(firstX.ToString("F0", inv)).Append('\n');
        sb.Append("LASTX\t").Append(lastX.ToString("F0", inv)).Append('\n');
        sb.Append("NPOINTS\t").Append(pointCount.ToString(inv)).Append('\n');
        sb.Append("FIRSTY\t0.100000").Append('\n');
        sb.Append("MAXY\t1.20000").Append('\n');
        sb.Append("MINY\t-0.05000").Append('\n');
        sb.Append("XYDATA").Append('\n');

        // Synthetic absorbance: gaussian-ish band centered near 350 nm so
        // values span a realistic dynamic range. Deterministic (no RNG).
        const double bandCenter = 350.0;
        const double bandWidth = 40.0;
        for (var i = 0; i < pointCount; i++)
        {
            var x = firstX + i * deltaX;
            var dx = (x - bandCenter) / bandWidth;
            var y = 1.1 * Math.Exp(-0.5 * dx * dx) + 0.05;
            sb.Append(x.ToString("F2", inv))
              .Append('\t')
              .Append(y.ToString("F6", inv))
              .Append('\n');
        }

        // Empty line then Shift-JIS footer section that the parser walks
        // through (the [測定情報] / [付属品情報] block in production exports).
        sb.Append('\n');
        sb.Append("[測定情報]").Append('\n');
        sb.Append("機種名\tV-750").Append('\n');
        sb.Append("シリアル番号\tSN-PLACEHOLDER").Append('\n');
        sb.Append("測定モード\tAbs").Append('\n');
        sb.Append("UV/Vis バンド幅\t2 nm").Append('\n');
        sb.Append("レスポンス\tMedium").Append('\n');
        sb.Append("測定速度\t200 nm/min").Append('\n');
        sb.Append("測光\t自動").Append('\n');
        sb.Append("光源切替\t340 nm").Append('\n');

        return sb.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore failures during benchmark teardown.
        }
    }
}
