namespace NMRAnalyzer.Core;

/// <summary>
/// Re-references an NMR spectrum's chemical-shift axis. The usual case is
/// pinning an internal standard (TMS) to 0 ppm: measure where the reference
/// peak currently sits, compute the shift, and slide the whole axis by it.
/// </summary>
public static class ChemicalShiftReferencer
{
    /// <summary>
    /// The shift (ppm) that moves <paramref name="observedReferencePpm"/> onto
    /// <paramref name="targetPpm"/> — 0 by default, i.e. TMS.
    /// </summary>
    public static double ComputeShift(double observedReferencePpm, double targetPpm = 0.0) =>
        targetPpm - observedReferencePpm;

    /// <summary>
    /// Return a copy of <paramref name="dataset"/> with every ppm value shifted
    /// by <paramref name="shiftPpm"/>. Intensities are untouched; only the axis
    /// endpoints move, and <see cref="NmrDataset.XValues"/> is derived from
    /// them. A zero or non-finite shift returns the original instance.
    /// </summary>
    public static NmrDataset ApplyShift(NmrDataset dataset, double shiftPpm)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!double.IsFinite(shiftPpm) || shiftPpm == 0.0)
        {
            return dataset;
        }

        return new NmrDataset
        {
            SourceFilePath = dataset.SourceFilePath,
            Title = dataset.Title,
            Dimensions = dataset.Dimensions,
            AxisStartPpm = dataset.AxisStartPpm + shiftPpm,
            AxisStopPpm = dataset.AxisStopPpm + shiftPpm,
            ObservedFrequencyMHz = dataset.ObservedFrequencyMHz,
            Nucleus = dataset.Nucleus,
            RealValues = dataset.RealValues,
            ImaginaryValues = dataset.ImaginaryValues,
            IsPpmAxis = dataset.IsPpmAxis,
        };
    }
}
