using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using GpcAnalyzer.Core;

namespace GpcAnalyzer.Benchmarks;

/// <summary>
/// Baseline benchmarks for <see cref="CsvGpcDataReader"/>.
/// Three parameter sizes simulate realistic LabSolutions exports: small
/// (1k points, typical analytical run), medium (10k points, long run), and
/// large (50k points, the upper end called out in ROADMAP §2-GPC).
///
/// The reader currently only exposes <c>Read(string filePath)</c>, so each
/// benchmark iteration reads from a single temp file written once in
/// <see cref="GlobalSetup"/>; disk I/O is therefore part of the timing but
/// dominated by the parser at large point counts.
/// </summary>
[MemoryDiagnoser]
public class CsvGpcDataReaderBenchmarks
{
    private readonly CsvGpcDataReader _reader = new();
    private string _smallPath = string.Empty;
    private string _mediumPath = string.Empty;
    private string _largePath = string.Empty;

    [Params(1_000, 10_000, 50_000)]
    public int PointCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _smallPath = WriteSyntheticLabSolutionsFile(1_000);
        _mediumPath = WriteSyntheticLabSolutionsFile(10_000);
        _largePath = WriteSyntheticLabSolutionsFile(50_000);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        TryDelete(_smallPath);
        TryDelete(_mediumPath);
        TryDelete(_largePath);
    }

    [Benchmark]
    public GpcDataset Read()
    {
        var path = PointCount switch
        {
            1_000 => _smallPath,
            10_000 => _mediumPath,
            _ => _largePath,
        };
        return _reader.Read(path);
    }

    private static string WriteSyntheticLabSolutionsFile(int pointCount)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"labplot-bench-{pointCount}-{Guid.NewGuid():N}.txt");
        var content = BuildSyntheticLabSolutions(pointCount);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string BuildSyntheticLabSolutions(int pointCount)
    {
        var sb = new StringBuilder(capacity: pointCount * 24);
        var inv = CultureInfo.InvariantCulture;

        // Mirror real LabSolutions TXT section structure so we exercise the
        // LabSolutions-specific code path. File-name and identifying fields
        // use neutral placeholders (no personal data).
        sb.AppendLine("[Header]");
        sb.AppendLine("Application Name\tLabSolutions");
        sb.AppendLine("Version\t5.92");
        sb.AppendLine("Data File Name\tC:\\Samples\\synthetic.lcd");
        sb.AppendLine("Output Date\t2026/05/27");
        sb.AppendLine("Output Time\t12:00:00");
        sb.AppendLine();
        sb.AppendLine("[Configuration]");
        sb.AppendLine("Instrument Name\tLC_1");
        sb.AppendLine("Instrument #\t1");
        sb.AppendLine("Line #\t1");
        sb.AppendLine("# of Detectors\t1");
        sb.AppendLine("Detector ID\tDetector A");
        sb.AppendLine("Detector Name\tDetectorA");
        sb.AppendLine("# of Channels\t1");
        sb.AppendLine();

        // A minimal molecular-weight statistics block keeps the reader's
        // section-detection path realistic.
        sb.AppendLine("[Average Molecular Weight Table(Detector A)]");
        sb.AppendLine("# of Peaks\t1");
        sb.AppendLine("Peak#\tMn\tMw\tMz\tMz1\tMv\tMw/Mn\tMv/Mn\tMz/Mw\tI.Visc\t%");
        sb.AppendLine("Total\t10000\t12000\t15000\t18000\t0\t1.20000\t0.00000\t1.25000\t1.00000\t100.0000");
        sb.AppendLine("1\t10000\t12000\t15000\t18000\t0\t1.20000\t0.00000\t1.25000\t1.00000\t100.0000");
        sb.AppendLine();

        // The chromatogram section: parser hot path lives here.
        const double intervalMin = 0.00833;   // 500 msec, matches real exports
        var endTime = intervalMin * (pointCount - 1);
        sb.AppendLine("[LC Chromatogram(Detector A-Ch1)]");
        sb.Append("Interval(msec)\t").AppendLine("500");
        sb.Append("# of Points\t").AppendLine(pointCount.ToString(inv));
        sb.Append("Start Time(min)\t").AppendLine("0.000");
        sb.Append("End Time(min)\t").AppendLine(endTime.ToString("F3", inv));
        sb.AppendLine("Intensity Units\tmV");
        sb.AppendLine("Intensity Multiplier\t0.001");
        sb.AppendLine("Wavelength(nm)\t270nm");
        sb.AppendLine("R.Time (min)\tIntensity");

        // Synthetic chromatogram: gaussian-ish peak centered mid-run so values
        // span a realistic dynamic range and force every line through the
        // double-parse path. Deterministic (no RNG) for reproducible bench.
        var center = pointCount / 2.0;
        var width = pointCount / 12.0;
        for (var i = 0; i < pointCount; i++)
        {
            var t = i * intervalMin;
            var dx = (i - center) / width;
            var intensity = (int)Math.Round(20000.0 * Math.Exp(-0.5 * dx * dx));
            sb.Append(t.ToString("F5", inv))
              .Append('\t')
              .Append(intensity.ToString(inv))
              .Append('\n');
        }

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
