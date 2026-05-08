using LabPlot.Tools.DlsSampleGenerator;

// Default output is the canonical samples/ slot relative to this tool's
// project directory. Override via the first command-line argument when
// regenerating into a different location.
var defaultOutput = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "LabPlot.DLS", "samples", "demo.xlsx"));

var outputPath = args.Length > 0 ? args[0] : defaultOutput;

DlsSampleBuilder.WriteDemoWorkbook(outputPath);
Console.WriteLine($"Wrote {Path.GetFullPath(outputPath)}");
