using BenchmarkDotNet.Attributes;
using ClosedXML.Excel;
using DlsAnalyzer.Core;

namespace DlsAnalyzer.Benchmarks;

/// <summary>
/// Baseline benchmarks for <see cref="ZetasizerXlsxReader"/>.
/// Two parameter sizes simulate realistic Zetasizer Pro xlsx exports:
/// 1 sheet × 3 runs (single measurement) and 5 sheets × 3 runs (a small
/// batch). Each sheet carries a size distribution block (60 bins ×
/// Number / Intensity / Volume columns) plus a correlation block
/// (typical g₂-1 vs. τ; ~150 τ samples per run).
///
/// <see cref="ZetasizerXlsxReader.Read"/> only takes a file path, so each
/// iteration reads from a single temp xlsx file written once in
/// <see cref="GlobalSetup"/>; ClosedXML xlsx parsing dominates the timing.
/// Synthetic fixtures are generated in-process (no committed test data).
/// </summary>
[MemoryDiagnoser]
public class ZetasizerXlsxReaderBenchmarks
{
    private readonly ZetasizerXlsxReader _reader = new();
    private string _singleSheetPath = string.Empty;
    private string _batchPath = string.Empty;

    [Params(1, 5)]
    public int SheetCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _singleSheetPath = WriteSyntheticZetasizerXlsx(1);
        _batchPath = WriteSyntheticZetasizerXlsx(5);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        TryDelete(_singleSheetPath);
        TryDelete(_batchPath);
    }

    [Benchmark]
    public IReadOnlyList<DlsDataset> Read()
    {
        var path = SheetCount switch
        {
            1 => _singleSheetPath,
            _ => _batchPath,
        };
        return _reader.Read(path);
    }

    private static string WriteSyntheticZetasizerXlsx(int sheetCount)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"labplot-dls-bench-{sheetCount}-{Guid.NewGuid():N}.xlsx");

        using var workbook = new XLWorkbook();
        for (int s = 0; s < sheetCount; s++)
        {
            // Neutral placeholder sample labels (no personal data).
            var ws = workbook.AddWorksheet($"Sample{s + 1:00}");
            PopulateSyntheticSheet(ws, s);
        }

        workbook.SaveAs(path);
        return path;
    }

    private static void PopulateSyntheticSheet(IXLWorksheet ws, int sampleIndex)
    {
        // Layout mirrors a Zetasizer Pro export: per sample we emit
        // 3 runs × (Size, Number%) / (Size, Intensity%) / (Size, Volume%)
        // followed by 3 runs × (Time, g₂-1). The reader pairs columns
        // by header keyword.
        const int sizeBinCount = 60;
        const int correlationPointCount = 150;
        const int runsPerBlock = 3;

        var sizeBins = LogSpace(0.4, 10_000.0, sizeBinCount);
        var taus = LogSpace(0.5, 5_000_000.0, correlationPointCount); // μs

        // Synthetic single-peak distribution centered at 100 nm (log-Gaussian)
        // with run-to-run jitter so each repeat is distinct.
        var distribution = BuildLogGaussian(sizeBins, peakNm: 80.0 + sampleIndex * 5.0, sigma: 0.25);

        var col = 1;
        for (int variant = 0; variant < 3; variant++) // Number / Intensity / Volume
        {
            var label = variant switch
            {
                0 => "Number (%)",
                1 => "Intensity (%)",
                _ => "Volume (%)",
            };
            for (int r = 0; r < runsPerBlock; r++)
            {
                ws.Cell(1, col).Value = "Size (d.nm)";
                ws.Cell(1, col + 1).Value = label;
                for (int i = 0; i < sizeBinCount; i++)
                {
                    ws.Cell(i + 2, col).Value = sizeBins[i];
                    var jitter = 1.0 + 0.02 * Math.Sin(r * 1.7 + i * 0.13);
                    ws.Cell(i + 2, col + 1).Value = distribution[i] * jitter;
                }
                col += 2;
            }
        }

        // Correlation function: g₂-1 (τ) = β · exp(-2·Γ·τ) for the centre
        // particle. Γ in 1/μs. β = 0.9, run-to-run jitter ±2%.
        var gamma = 0.005 + sampleIndex * 0.0005;
        const double beta = 0.9;
        for (int r = 0; r < runsPerBlock; r++)
        {
            ws.Cell(1, col).Value = "Time (μs)"; // μs
            ws.Cell(1, col + 1).Value = "Correlation Coefficient";
            for (int i = 0; i < correlationPointCount; i++)
            {
                ws.Cell(i + 2, col).Value = taus[i];
                var jitter = 1.0 + 0.02 * Math.Sin(r * 1.3 + i * 0.07);
                ws.Cell(i + 2, col + 1).Value = beta * Math.Exp(-2 * gamma * taus[i]) * jitter;
            }
            col += 2;
        }
    }

    private static double[] LogSpace(double min, double max, int count)
    {
        var logMin = Math.Log10(min);
        var logMax = Math.Log10(max);
        var step = (logMax - logMin) / (count - 1);
        var result = new double[count];
        for (int i = 0; i < count; i++) result[i] = Math.Pow(10, logMin + i * step);
        return result;
    }

    private static double[] BuildLogGaussian(double[] sizeBins, double peakNm, double sigma)
    {
        var result = new double[sizeBins.Length];
        var logPeak = Math.Log10(peakNm);
        var sum = 0.0;
        for (int i = 0; i < sizeBins.Length; i++)
        {
            var z = (Math.Log10(sizeBins[i]) - logPeak) / sigma;
            result[i] = Math.Exp(-0.5 * z * z);
            sum += result[i];
        }
        if (sum > 0)
        {
            // Normalize to percent.
            for (int i = 0; i < sizeBins.Length; i++) result[i] = result[i] / sum * 100.0;
        }
        return result;
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
