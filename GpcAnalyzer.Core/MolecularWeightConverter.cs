namespace GpcAnalyzer.Core;

public sealed class MolecularWeightConverter
{
    public const double DefaultMinMolecularWeight = 100;
    public const double DefaultMaxMolecularWeight = 100_000_000;

    public MolecularWeightDataset Convert(
        GpcDataset dataset,
        CalibrationCurve curve,
        double minMolecularWeight = DefaultMinMolecularWeight,
        double maxMolecularWeight = DefaultMaxMolecularWeight)
    {
        if (minMolecularWeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minMolecularWeight), "Minimum molecular weight must be positive.");
        }

        if (maxMolecularWeight <= minMolecularWeight)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMolecularWeight), "Maximum molecular weight must be greater than the minimum.");
        }

        var points = dataset.Points
            .Select(point => new MolecularWeightDataPoint
            {
                RetentionTime = point.X,
                MolecularWeight = curve.CalculateMolecularWeight(point.X),
                Signal = point.Y,
            })
            .Where(point => double.IsFinite(point.MolecularWeight)
                && point.MolecularWeight >= minMolecularWeight
                && point.MolecularWeight <= maxMolecularWeight)
            .OrderBy(point => point.MolecularWeight)
            .ToArray();

        if (points.Length == 0)
        {
            throw new InvalidDataException(
                $"No molecular weight points were found in the selected range ({minMolecularWeight:G} - {maxMolecularWeight:G}).");
        }

        return new MolecularWeightDataset
        {
            SourceFilePath = dataset.SourceFilePath,
            Solvent = curve.Solvent,
            Detector = curve.Detector,
            MinMolecularWeight = minMolecularWeight,
            MaxMolecularWeight = maxMolecularWeight,
            SourcePointCount = dataset.Points.Count,
            YLabel = dataset.YLabel,
            Points = points,
        };
    }
}
