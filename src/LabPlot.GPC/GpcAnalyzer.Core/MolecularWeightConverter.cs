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
        // dw/dlogM is the differential weight fraction with respect to logM.
        // Properly defined as dw_i / |dlogM_i| where dw_i = S(t_i)·|dt_i| / A
        // (S = detector signal, A = total chromatogram area). The previous
        // implementation used raw signal divided by |dlogM| between adjacent
        // raw points, which (a) dropped the dt area element so non-uniform
        // retention-time sampling skewed the distribution shape, and (b)
        // attributed the next point's signal to the current point's MW —
        // a one-bin alignment offset.
        //
        // The new implementation sorts ascending by retention time, uses a
        // half-interval dt at each point (with endpoint half-widths), and
        // central-difference dlogM. Each output bin keeps its own MW/RT.
        var validPoints = sourcePoints
            .Where(p => double.IsFinite(p.RetentionTime)
                        && double.IsFinite(p.MolecularWeight)
                        && p.MolecularWeight > 0
                        && double.IsFinite(p.LogMolecularWeight)
                        && double.IsFinite(p.Signal))
            .OrderBy(static p => p.RetentionTime)
            .ToArray();

        if (validPoints.Length < 2)
        {
            return Array.Empty<MolecularWeightDataPoint>();
        }

        var dts = new double[validPoints.Length];
        for (var i = 0; i < validPoints.Length; i++)
        {
            var left = i > 0 ? validPoints[i - 1].RetentionTime : validPoints[i].RetentionTime;
            var right = i < validPoints.Length - 1
                ? validPoints[i + 1].RetentionTime
                : validPoints[i].RetentionTime;
            dts[i] = 0.5 * (right - left);
        }

        var totalArea = 0.0;
        for (var i = 0; i < validPoints.Length; i++)
        {
            totalArea += validPoints[i].Signal * Math.Abs(dts[i]);
        }

        if (!double.IsFinite(totalArea) || Math.Abs(totalArea) <= double.Epsilon)
        {
            throw new InvalidDataException("Cannot calculate dw/dlogM because the total signal area is zero.");
        }

        var points = new List<MolecularWeightDataPoint>(validPoints.Length);
        for (var i = 0; i < validPoints.Length; i++)
        {
            var p = validPoints[i];
            if (!IsMolecularWeightInRange(p, minMolecularWeight, maxMolecularWeight))
            {
                continue;
            }

            double dLogM;
            if (i == 0)
            {
                dLogM = validPoints[1].LogMolecularWeight - p.LogMolecularWeight;
            }
            else if (i == validPoints.Length - 1)
            {
                dLogM = p.LogMolecularWeight - validPoints[i - 1].LogMolecularWeight;
            }
            else
            {
                dLogM = 0.5 * (validPoints[i + 1].LogMolecularWeight - validPoints[i - 1].LogMolecularWeight);
            }

            if (!double.IsFinite(dLogM) || Math.Abs(dLogM) <= double.Epsilon)
            {
                continue;
            }

            var dw = p.Signal * Math.Abs(dts[i]) / totalArea;
            var value = dw / Math.Abs(dLogM);
            if (!double.IsFinite(value))
            {
                continue;
            }

            points.Add(new MolecularWeightDataPoint
            {
                RetentionTime = p.RetentionTime,
                MolecularWeight = p.MolecularWeight,
                LogMolecularWeight = p.LogMolecularWeight,
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
