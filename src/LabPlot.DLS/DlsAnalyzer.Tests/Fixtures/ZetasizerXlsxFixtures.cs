using ClosedXML.Excel;

namespace DlsAnalyzer.Tests.Fixtures;

/// <summary>
/// Builds in-memory Zetasizer-shaped xlsx files for tests so we do not
/// have to commit real measurement data into the repo.
/// </summary>
internal static class ZetasizerXlsxFixtures
{
    public static readonly double[] DefaultSizeBins = { 0.3, 0.3489, 0.4057, 0.4718, 0.5487, 0.6381 };

    public static void WriteThreeRunNumberDistribution(string path, string sheetName)
    {
        using var wb = new XLWorkbook();
        AddNumberDistributionSheet(wb, sheetName, runCount: 3);
        wb.SaveAs(path);
    }

    public static void WriteMultipleSheets(string path, IReadOnlyList<string> sheetNames)
    {
        using var wb = new XLWorkbook();
        foreach (var name in sheetNames)
            AddNumberDistributionSheet(wb, name, runCount: 3);
        wb.SaveAs(path);
    }

    public static void WriteFullExport(string path, string sheetName)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheetName);
        var sizes = DefaultSizeBins;
        var times = new[] { 0.0875, 0.1, 0.1125, 0.13, 0.15 };

        // Block 1: Number (col 1-2)
        WritePair(ws, col: 1, header: $"Size (d.nm) - {sheetName} [Steady state]",
            yHeader: $"Number (Percent) - {sheetName} [Steady state]",
            x: sizes, y: new[] { 0.0, 0.0, 0.5, 5.0, 20.0, 25.0 });

        // Block 2: Intensity (col 4-5, gap col 3)
        WritePair(ws, col: 4, header: $"Size (d.nm) - {sheetName} [Steady state]",
            yHeader: $"Intensity (Percent) - {sheetName} [Steady state]",
            x: sizes, y: new[] { 0.0, 0.0, 0.0, 1.0, 5.0, 12.0 });

        // Block 3: Time / Correlation (col 7-8, gap col 6)
        WritePair(ws, col: 7, header: $"Time (µs) - {sheetName} [Steady state]",
            yHeader: $"Correlation Coefficient (g₂-1) - {sheetName} [Steady state]",
            x: times, y: new[] { 0.95, 0.94, 0.92, 0.88, 0.80 });

        wb.SaveAs(path);
    }

    public static void WriteEmptyWorkbook(string path)
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Empty");
        wb.SaveAs(path);
    }

    private static void AddNumberDistributionSheet(XLWorkbook wb, string sheetName, int runCount)
    {
        var ws = wb.AddWorksheet(sheetName);
        var sizes = DefaultSizeBins;
        var sizeHeader = $"Size (d.nm) - {sheetName} [Steady state]";
        var yHeader = $"Number (Percent) - {sheetName} [Steady state]";

        for (int run = 0; run < runCount; run++)
        {
            var sizeCol = run * 2 + 1;
            var yCol = sizeCol + 1;
            ws.Cell(1, sizeCol).Value = sizeHeader;
            ws.Cell(1, yCol).Value = yHeader;
            for (int i = 0; i < sizes.Length; i++)
            {
                ws.Cell(i + 2, sizeCol).Value = sizes[i];
                ws.Cell(i + 2, yCol).Value = SyntheticDistributionValue(sizes[i], runOffset: run);
            }
        }
    }

    private static void WritePair(IXLWorksheet ws, int col, string header, string yHeader, double[] x, double[] y)
    {
        ws.Cell(1, col).Value = header;
        ws.Cell(1, col + 1).Value = yHeader;
        for (int i = 0; i < x.Length; i++)
        {
            ws.Cell(i + 2, col).Value = x[i];
            ws.Cell(i + 2, col + 1).Value = y[i];
        }
    }

    private static double SyntheticDistributionValue(double size, int runOffset)
    {
        // a tiny lognormal-ish bump centred around d ~ 4 nm so the test data
        // looks like a real Zetasizer trace without being identical run-to-run
        var center = 4.0 + 0.05 * runOffset;
        var dx = Math.Log(size) - Math.Log(center);
        return Math.Round(25.0 * Math.Exp(-(dx * dx) * 8.0), 4);
    }
}
