using NMRAnalyzer.Core;

namespace NMRAnalyzer.Tests;

public class ChemicalShiftReferencerTests
{
    [Fact]
    public void ComputeShift_MovesObservedOntoTarget()
    {
        Assert.Equal(-0.05, ChemicalShiftReferencer.ComputeShift(observedReferencePpm: 0.05), precision: 6);
        Assert.Equal(0.10, ChemicalShiftReferencer.ComputeShift(observedReferencePpm: 7.16, targetPpm: 7.26), precision: 6);
    }

    [Fact]
    public void ApplyShift_SlidesWholePpmAxis()
    {
        var dataset = new NmrDataset
        {
            AxisStartPpm = 10.0,
            AxisStopPpm = 0.0,
            RealValues = new[] { 1.0, 2.0, 3.0 },
        };

        // TMS observed at 0.05 -> shift by -0.05 to pin it at 0.
        var shift = ChemicalShiftReferencer.ComputeShift(0.05);
        var shifted = ChemicalShiftReferencer.ApplyShift(dataset, shift);

        Assert.Equal(9.95, shifted.AxisStartPpm, precision: 6);
        Assert.Equal(-0.05, shifted.AxisStopPpm, precision: 6);
        Assert.Equal(9.95, shifted.XValues[0], precision: 6);
        Assert.Equal(-0.05, shifted.XValues[^1], precision: 6);
        // Intensities are untouched.
        Assert.Equal(dataset.RealValues, shifted.RealValues);
    }

    [Fact]
    public void ApplyShift_ZeroShiftReturnsSameInstance()
    {
        var dataset = new NmrDataset { AxisStartPpm = 8.0, AxisStopPpm = 0.0, RealValues = new[] { 1.0 } };
        Assert.Same(dataset, ChemicalShiftReferencer.ApplyShift(dataset, 0.0));
    }
}
