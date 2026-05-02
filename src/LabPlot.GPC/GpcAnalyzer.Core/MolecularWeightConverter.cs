namespace GpcAnalyzer.Core;

public sealed class MolecularWeightConverter
{
    public const double DefaultMinMolecularWeight = 1;
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

        var sourcePoints = CreateSourcePoints(dataset.Points, curve);
        var signalPoints = CreateSignalPoints(sourcePoints, minMolecularWeight, maxMolecularWeight);
        var points = yMode switch
        {
            MolecularWeightYMode.Signal => signalPoints,
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
            Statistics = dataset.MolecularWeightStatistics ?? CalculateStatistics(signalPoints),
            Points = points,
        };
    }

    private static MolecularWeightDataPoint[] CreateSourcePoints(IReadOnlyList<GpcDataPoint> points, CalibrationCurve curve)
    {
        var sourcePoints = new MolecularWeightDataPoint[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var logMolecularWeight = curve.CalculateLogMolecularWeight(point.X);
            sourcePoints[i] = new MolecularWeightDataPoint
            {
                RetentionTime = point.X,
                MolecularWeight = Math.Pow(10, logMolecularWeight),
                LogMolecularWeight = logMolecularWeight,
                Signal = point.Y,
            };
        }

        return sourcePoints;
    }

    private static MolecularWeightStatistics? CalculateStatistics(IReadOnlyList<MolecularWeightDataPoint> points)
    {
        var totalWeight = 0.0;
        var mnDenominator = 0.0;
        var mwNumerator = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (!double.IsFinite(point.MolecularWeight)
                || point.MolecularWeight <= 0
                || !double.IsFinite(point.Signal)
                || point.Signal <= 0)
            {
                continue;
            }

            totalWeight += point.Signal;
            mnDenominator += point.Signal / point.MolecularWeight;
            mwNumerator += point.Signal * point.MolecularWeight;
        }

        if (!double.IsFinite(totalWeight) || totalWeight <= double.Epsilon)
        {
            return null;
        }

        if (!double.IsFinite(mnDenominator) || mnDenominator <= double.Epsilon)
        {
            return null;
        }

        var mn = totalWeight / mnDenominator;
        var mw = mwNumerator / totalWeight;
        if (!double.IsFinite(mn) || !double.IsFinite(mw))
        {
            return null;
        }

        return new MolecularWeightStatistics
        {
            Mn = mn,
            Mw = mw,
            Pdi = mn > double.Epsilon ? mw / mn : null,
            Source = MolecularWeightStatisticsSource.Calculated,
        };
    }

    private static MolecularWeightDataPoint[] CreateSignalPoints(
        IReadOnlyList<MolecularWeightDataPoint> sourcePoints,
        double minMolecularWeight,
        double maxMolecularWeight)
    {
        var points = new List<MolecularWeightDataPoint>(sourcePoints.Count);
        for (var i = 0; i < sourcePoints.Count; i++)
        {
            var point = sourcePoints[i];
            if (IsMolecularWeightInRange(point, minMolecularWeight, maxMolecularWeight))
            {
                points.Add(point);
            }
        }

        points.Sort(static (left, right) => left.MolecularWeight.CompareTo(right.MolecularWeight));
        return points.ToArray();
    }

    private static MolecularWeightDataPoint[] CreateDifferentialWeightFractionPoints(
        IReadOnlyList<MolecularWeightDataPoint> sourcePoints,
        double minMolecularWeight,
        double maxMolecularWeight)
    {
        var totalWeight = 0.0;
        for (var i = 0; i < sourcePoints.Count; i++)
        {
            totalWeight += sourcePoints[i].Signal;
        }

        if (!double.IsFinite(totalWeight) || Math.Abs(totalWeight) <= double.Epsilon)
        {
            throw new InvalidDataException("Cannot calculate dw/dlogM because the total signal is zero.");
        }

        var orderedSourcePoints = sourcePoints.ToArray();
        Array.Sort(
            orderedSourcePoints,
            static (left, right) => right.RetentionTime.CompareTo(left.RetentionTime));

        var points = new List<MolecularWeightDataPoint>(Math.Max(0, orderedSourcePoints.Length - 1));
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
            var dLogM = next.LogMolecularWeight - current.LogMolecularWeight;
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
                LogMolecularWeight = current.LogMolecularWeight,
                Signal = value,
            });
        }

        points.Sort(static (left, right) => left.MolecularWeight.CompareTo(right.MolecularWeight));
        return points.ToArray();
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
