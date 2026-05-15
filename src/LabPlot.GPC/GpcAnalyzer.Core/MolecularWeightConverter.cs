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
        if (!double.IsFinite(minMolecularWeight) || minMolecularWeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minMolecularWeight),
                "Minimum molecular weight must be finite and positive.");
        }

        if (!double.IsFinite(maxMolecularWeight) || maxMolecularWeight <= minMolecularWeight)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMolecularWeight),
                "Maximum molecular weight must be finite and greater than the minimum.");
        }

        var (sourcePoints, overflowedCount, directionReversalCount) =
            CreateSourcePoints(dataset.Points, curve);
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
            OverflowedPointCount = overflowedCount,
            CalibrationDirectionReversalCount = directionReversalCount,
            YLabel = yMode == MolecularWeightYMode.DifferentialWeightFraction ? "dw/dlogM" : dataset.YLabel,
            YMode = yMode,
            Statistics = dataset.MolecularWeightStatistics ?? CalculateStatistics(signalPoints),
            Points = points,
        };
    }

    private static (MolecularWeightDataPoint[] SourcePoints, int OverflowedCount, int DirectionReversalCount) CreateSourcePoints(
        IReadOnlyList<GpcDataPoint> points,
        CalibrationCurve curve)
    {
        var sourcePoints = new MolecularWeightDataPoint[points.Count];
        var overflowedCount = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var logMolecularWeight = curve.CalculateLogMolecularWeight(point.X);
            double molecularWeight;
            if (!double.IsFinite(logMolecularWeight))
            {
                molecularWeight = double.NaN;
                overflowedCount++;
            }
            else
            {
                molecularWeight = Math.Pow(10, logMolecularWeight);
                if (!double.IsFinite(molecularWeight))
                {
                    // Math.Pow(10, x) blows up to Infinity around x ~= 309; that
                    // would otherwise propagate through area sums and corrupt
                    // every downstream statistic. Coerce to NaN so the point
                    // gets filtered out cleanly and surface the count.
                    molecularWeight = double.NaN;
                    overflowedCount++;
                }
            }

            sourcePoints[i] = new MolecularWeightDataPoint
            {
                RetentionTime = point.X,
                MolecularWeight = molecularWeight,
                LogMolecularWeight = logMolecularWeight,
                Signal = point.Y,
            };
        }

        var directionReversalCount = CountCalibrationDirectionReversals(sourcePoints);
        return (sourcePoints, overflowedCount, directionReversalCount);
    }

    /// <summary>
    /// Walks the source points in retention-time order and counts adjacent
    /// pairs whose logM derivative sign disagrees with the dominant direction
    /// established by the first finite step. For a well-behaved GPC trace
    /// every step should move logM the same way; reversals usually mean some
    /// data points landed in the extrapolation tail of the cubic fit.
    /// </summary>
    private static int CountCalibrationDirectionReversals(IReadOnlyList<MolecularWeightDataPoint> sourcePoints)
    {
        var ordered = sourcePoints
            .Where(p => double.IsFinite(p.RetentionTime) && double.IsFinite(p.LogMolecularWeight))
            .OrderBy(static p => p.RetentionTime)
            .ToArray();
        if (ordered.Length < 2)
        {
            return 0;
        }

        var reversals = 0;
        int? expectedSign = null;
        for (var i = 1; i < ordered.Length; i++)
        {
            var dt = ordered[i].RetentionTime - ordered[i - 1].RetentionTime;
            if (dt <= 0)
            {
                continue;
            }

            var dLogM = ordered[i].LogMolecularWeight - ordered[i - 1].LogMolecularWeight;
            if (Math.Abs(dLogM) < 1e-12)
            {
                continue;
            }

            var sign = Math.Sign(dLogM);
            if (expectedSign is null)
            {
                expectedSign = sign;
                continue;
            }

            if (sign != expectedSign)
            {
                reversals++;
            }
        }

        return reversals;
    }

    private static MolecularWeightStatistics? CalculateStatistics(IReadOnlyList<MolecularWeightDataPoint> points)
    {
        // For non-uniform chromatogram sampling we weight by w_i = S(t_i)·dt_i
        // so Mn = Σ(S·dt) / Σ((S·dt)/M) and Mw = Σ((S·dt)·M) / Σ(S·dt)
        // approximate the area-element-weighted moments. When dt is constant
        // (uniform sampling) the dt factor cancels and the result matches the
        // previous signal-only implementation.
        var ordered = points
            .Where(static p => double.IsFinite(p.MolecularWeight)
                                && p.MolecularWeight > 0
                                && double.IsFinite(p.Signal)
                                && p.Signal > 0
                                && double.IsFinite(p.RetentionTime))
            .OrderBy(static p => p.RetentionTime)
            .ToArray();
        if (ordered.Length < 2)
        {
            return null;
        }

        var dts = new double[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var left = i > 0 ? ordered[i - 1].RetentionTime : ordered[i].RetentionTime;
            var right = i < ordered.Length - 1
                ? ordered[i + 1].RetentionTime
                : ordered[i].RetentionTime;
            dts[i] = 0.5 * (right - left);
        }

        var totalWeight = 0.0;
        var mnDenominator = 0.0;
        var mwNumerator = 0.0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var areaElement = ordered[i].Signal * Math.Abs(dts[i]);
            if (!double.IsFinite(areaElement) || areaElement <= 0)
            {
                continue;
            }
            totalWeight += areaElement;
            mnDenominator += areaElement / ordered[i].MolecularWeight;
            mwNumerator += areaElement * ordered[i].MolecularWeight;
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
