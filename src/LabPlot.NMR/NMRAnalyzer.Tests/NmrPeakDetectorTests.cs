using NMRAnalyzer.Core;

namespace NMRAnalyzer.Tests;

public class NmrPeakDetectorTests
{
    // Build a descending-ppm dataset directly (bypassing the reader) from
    // explicit ppm/intensity pairs. ppm must be supplied descending to match
    // the .jdf convention.
    private static NmrDataset Dataset(double[] ppmDescending, double[] intensity) => new()
    {
        AxisStartPpm = ppmDescending[0],
        AxisStopPpm = ppmDescending[^1],
        RealValues = intensity,
    };

    [Fact]
    public void Find_LocatesTwoGaussianPeaksSortedByIntensity()
    {
        // Two bumps: a taller one near 7 ppm, a shorter one near 2 ppm.
        var ppm = new double[21];
        var y = new double[21];
        for (var i = 0; i < 21; i++)
        {
            ppm[i] = 10.0 - i * 0.5; // 10.0 down to 0.0
            var p = ppm[i];
            y[i] = 5.0 * Math.Exp(-Math.Pow(p - 7.0, 2) / 0.5)
                   + 2.0 * Math.Exp(-Math.Pow(p - 2.0, 2) / 0.5);
        }

        var peaks = NmrPeakDetector.Find(Dataset(ppm, y), new NmrPeakFinderConfig { Window = 2 });

        Assert.Equal(2, peaks.Count);
        Assert.True(peaks[0].Intensity > peaks[1].Intensity);
        Assert.Equal(7.0, peaks[0].Ppm, precision: 1);
        Assert.Equal(2.0, peaks[1].Ppm, precision: 1);
    }

    [Fact]
    public void Find_HonorsMaxPeaks()
    {
        var ppm = new double[21];
        var y = new double[21];
        for (var i = 0; i < 21; i++)
        {
            ppm[i] = 10.0 - i * 0.5;
            var p = ppm[i];
            y[i] = 5.0 * Math.Exp(-Math.Pow(p - 7.0, 2) / 0.5)
                   + 2.0 * Math.Exp(-Math.Pow(p - 2.0, 2) / 0.5);
        }

        var peaks = NmrPeakDetector.Find(
            Dataset(ppm, y), new NmrPeakFinderConfig { Window = 2, MaxPeaks = 1 });

        Assert.Single(peaks);
        Assert.Equal(7.0, peaks[0].Ppm, precision: 1);
    }

    [Fact]
    public void RefineManualPeak_SnapsToLocalMaximum()
    {
        var ppm = new double[21];
        var y = new double[21];
        for (var i = 0; i < 21; i++)
        {
            ppm[i] = 10.0 - i * 0.5;
            y[i] = 5.0 * Math.Exp(-Math.Pow(ppm[i] - 7.0, 2) / 0.5);
        }

        // Click slightly off the true peak; expect a snap back to ~7 ppm.
        var refined = NmrPeakDetector.RefineManualPeak(Dataset(ppm, y), clickedPpm: 6.7, snapWindowPpm: 0.6);

        Assert.NotNull(refined);
        Assert.Equal(7.0, refined!.Ppm, precision: 1);
    }
}
