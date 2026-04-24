namespace GpcAnalyzer.Core;

public sealed class MolecularWeightConverter
{
    public const double DefaultMinMolecularWeight = 100;
    public const double DefaultMaxMolecularWeight = 100_000_000;

    public MolecularWeightDataset Convert(
        GpcDataset dataset,
        CalibrationCurve curve,
        MolecularWeightYMode yMode = MolecularWeightYMode.Signal,
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

        var sourcePoints = dataset.Points
            .Select(point => new MolecularWeightDataPoint
            {
                RetentionTime = point.X,
                MolecularWeight = curve.CalculateMolecularWeight(point.X),
                Signal = point.Y,
            })
            .ToArray();
        var points = yMode switch
        {
            MolecularWeightYMode.Signal => CreateSignalPoints(sourcePoints, minMolecularWeight, maxMolecularWeight),
            MolecularWeightYMode.DifferentialWeightFraction => CreateDifferentialWeightFractionPoints(
                sourcePoints,
                minMolecularWeight,
                maxMolecularWeight),
            _ => throw new ArgumentOutOfRangeException(nameof(yMode), yMode, "Unsupported molecular weight Y mode."),
        };

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
            YLabel = yMode == MolecularWeightYMode.DifferentialWeightFraction ? "dw/dlogM" : dataset.YLabel,
            YMode = yMode,
            Points = points,
        };
    }

    private static MolecularWeightDataPoint[] CreateSignalPoints(
        IReadOnlyList<MolecularWeightDataPoint> sourcePoints,
        double minMolecularWeight,
        double maxMolecularWeight)
    {
        return sourcePoints
            .Where(point => IsMolecularWeightInRange(point, minMolecularWeight, maxMolecularWeight))
            .OrderBy(point => point.MolecularWeight)
            .ToArray();
    }

    private static MolecularWeightDataPoint[] CreateDifferentialWeightFractionPoints(
        IReadOnlyList<MolecularWeightDataPoint> sourcePoints,
        double minMolecularWeight,
        double maxMolecularWeight)
    {
        var totalWeight = sourcePoints.Sum(point => point.Signal);
        if (!double.IsFinite(totalWeight) || Math.Abs(totalWeight) <= double.Epsilon)
        {
            throw new InvalidDataException("Cannot calculate dw/dlogM because the total signal is zero.");
        }

        var orderedSourcePoints = sourcePoints
            .OrderByDescending(point => point.RetentionTime)
            .ToArray();
        var points = new List<MolecularWeightDataPoint>();
        for (var i = 0; i < orderedSourcePoints.Length - 1; i++)
        {
            var current = orderedSourcePoints[i];
            var next = orderedSourcePoints[i + 1];
            if (!IsMolecularWeightInRange(current, minMolecularWeight, maxMolecularWeight))
            {
                continue;
            }

            if (!double.IsFinite(next.MolecularWeight) || next.MolecularWeight <= 0)
            {
                continue;
            }

            var dw = next.Signal / totalWeight;
            var dLogM = Math.Log10(next.MolecularWeight) - Math.Log10(current.MolecularWeight);
            if (!double.IsFinite(dw) || !double.IsFinite(dLogM) || Math.Abs(dLogM) <= double.Epsilon)
            {
                continue;
            }

            var value = dw / dLogM;
            if (!double.IsFinite(value))
            {
                continue;
            }

            points.Add(new MolecularWeightDataPoint
            {
                RetentionTime = current.RetentionTime,
                MolecularWeight = current.MolecularWeight,
                Signal = value,
            });
        }

        return points
            .OrderBy(point => point.MolecularWeight)
            .ToArray();
    }

    private static bool IsMolecularWeightInRange(
        MolecularWeightDataPoint point,
        double minMolecularWeight,
        double maxMolecularWeight)
    {
        return double.IsFinite(point.MolecularWeight)
            && point.MolecularWeight >= minMolecularWeight
            && point.MolecularWeight <= maxMolecularWeight;
    }
}
