using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class IrPeakFinderTests
{
    [Fact]
    public void Find_SingleGaussianPeak_RecoversCenterByParabolicInterpolation()
    {
        // Gaussian centred at 1730 cm⁻¹ (typical C=O), width 8 cm⁻¹, A = 0.9.
        var dataset = MakeIrAbsorbanceScan(SampleGaussian(400, 4000, 1.0, 1730, 8, 0.9));

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavenumberCm1, 1729.5, 1730.5);
        Assert.InRange(peaks[0].AbsorbanceValue, 0.85, 0.95);
    }

    [Fact]
    public void Find_TwoPeaks_RanksByAbsorbance()
    {
        // Tall C=O at 1730 (A=1.0), shorter C-H at 2920 (A=0.4).
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var y = 1.0 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 64))
                  + 0.4 * Math.Exp(-Math.Pow(x - 2920, 2) / (2 * 100));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeIrAbsorbanceScan(points);

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Equal(2, peaks.Count);
        Assert.InRange(peaks[0].WavenumberCm1, 1729.5, 1730.5);
        Assert.InRange(peaks[1].WavenumberCm1, 2919.5, 2920.5);
        Assert.True(peaks[0].AbsorbanceValue > peaks[1].AbsorbanceValue);
    }

    [Fact]
    public void Find_RespectsMinimumAbsorbanceFilter()
    {
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var y = 0.05 * Math.Exp(-Math.Pow(x - 2200, 2) / (2 * 64))
                  + 0.5 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 100));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeIrAbsorbanceScan(points);

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.1, // hides the small 2200 peak
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavenumberCm1, 1729.5, 1730.5);
    }

    [Fact]
    public void Find_RespectsMaxPeaksLimit()
    {
        // Three explicit peaks at 1730, 2200, 2900 with descending heights.
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var y = 1.0 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 25))
                  + 0.7 * Math.Exp(-Math.Pow(x - 2200, 2) / (2 * 25))
                  + 0.4 * Math.Exp(-Math.Pow(x - 2900, 2) / (2 * 25));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeIrAbsorbanceScan(points);

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 2,
        });

        Assert.Equal(2, peaks.Count);
        Assert.InRange(peaks[0].WavenumberCm1, 1729.5, 1730.5);
        Assert.InRange(peaks[1].WavenumberCm1, 2199.5, 2200.5);
    }

    [Fact]
    public void Find_RestrictsToWavenumberRange()
    {
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var y = 1.0 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 25))
                  + 0.5 * Math.Exp(-Math.Pow(x - 2900, 2) / (2 * 25));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeIrAbsorbanceScan(points);

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 5,
            WavenumberMinCm1 = 2500,
            WavenumberMaxCm1 = 3200,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavenumberCm1, 2899.5, 2900.5);
    }

    [Fact]
    public void Find_NonWavenumberDataset_ReturnsEmpty()
    {
        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Absorbance",
            Points = SampleGaussian(200, 400, 1.0, 280, 8, 0.9),
        };

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig());

        Assert.Empty(peaks);
    }

    [Fact]
    public void Find_TransmittanceIrScan_ConvertsToAbsorbance()
    {
        // Transmittance dip at 1730 cm⁻¹ → absorbance peak at 1730 cm⁻¹.
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            // T = 100 - 90 * Gaussian → ~10 % at peak, ~100 % at the wings.
            var dip = 90.0 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 64));
            points.Add(new SpectrumDataPoint { X = x, Y = 100.0 - dip });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "1/CM",
            RawYUnits = "TRANSMITTANCE",
            XLabel = "Wavenumber / cm⁻¹",
            YLabel = "Transmittance / %",
            Points = points,
        };

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.1,
            Window = 3,
            MaxPeaks = 1,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavenumberCm1, 1729.5, 1730.5);
    }

    [Fact]
    public void Find_BaselineRipple_RejectsLowProminenceBumps()
    {
        // Slow sinusoidal baseline (period ~200 cm⁻¹, amplitude 0.10) plus
        // a single sharp Gaussian on top at 1730 cm⁻¹. Without prominence,
        // the rolling baseline produces many local maxima above the
        // absolute threshold (the original behaviour the user reported).
        // With a 0.30 A prominence floor only the real peak survives.
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var ripple = 0.15 + 0.10 * Math.Sin(2 * Math.PI * x / 200.0);
            var peak = 0.6 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 64));
            points.Add(new SpectrumDataPoint { X = x, Y = ripple + peak });
        }

        var dataset = MakeIrAbsorbanceScan(points);

        // Sanity: with prominence disabled, the ripple alone produces many
        // false peaks above MinimumAbsorbance — exactly what we want to
        // suppress in the next call.
        var noFilter = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            MinimumProminence = 0.0,
            Window = 5,
            MaxPeaks = 0,
        });
        Assert.True(noFilter.Count > 5);

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            MinimumProminence = 0.30,
            Window = 5,
            MaxPeaks = 10,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavenumberCm1, 1729.5, 1730.5);
    }

    [Fact]
    public void Find_MinimumProminenceZero_DisablesProminenceFilter()
    {
        // With prominence = 0 the detector falls back to "any local max"
        // behaviour — kept as an escape hatch for spectra where prominence
        // is misleading.
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var y = 0.3 * Math.Exp(-Math.Pow(x - 1700, 2) / (2 * 100))
                  + 0.28 * Math.Exp(-Math.Pow(x - 1800, 2) / (2 * 100));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeIrAbsorbanceScan(points);

        var peaks = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            MinimumProminence = 0.0,
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Equal(2, peaks.Count);
    }

    [Fact]
    public void Result_HasResult_TrueForFiniteValues()
    {
        var ok = new IrPeakResult { WavenumberCm1 = 1730, AbsorbanceValue = 0.5, SampleIndex = 100 };
        var nan = new IrPeakResult { WavenumberCm1 = double.NaN, AbsorbanceValue = 0.5, SampleIndex = 0 };

        Assert.True(ok.HasResult);
        Assert.False(nan.HasResult);
    }

    [Fact]
    public void RefineManualPeak_ClickInsideSnapWindow_SnapsToLocalMaximum()
    {
        // Gaussian at 1730 cm⁻¹. Click at 1715 (inside ±20 cm⁻¹) should snap
        // back to ~1730.
        var dataset = MakeIrAbsorbanceScan(SampleGaussian(400, 4000, 1.0, 1730, 8, 0.9));

        var refined = IrPeakFinder.RefineManualPeak(dataset, clickedWavenumberCm1: 1715.0);

        Assert.NotNull(refined);
        Assert.InRange(refined!.WavenumberCm1, 1729.5, 1730.5);
        Assert.InRange(refined.AbsorbanceValue, 0.85, 0.95);
    }

    [Fact]
    public void RefineManualPeak_ClickOutsideSnapWindow_FallsBackToNearestPoint()
    {
        // Single Gaussian centred at 1730. Click at 3000 with a tight 0.05
        // cm⁻¹ snap window: empty window → nearest-neighbour fallback returns
        // the data point closest to the click on the 1 cm⁻¹ grid.
        var dataset = MakeIrAbsorbanceScan(SampleGaussian(400, 4000, 1.0, 1730, 8, 0.9));

        var refined = IrPeakFinder.RefineManualPeak(dataset, clickedWavenumberCm1: 3000.4, snapWindowCm1: 0.05);

        Assert.NotNull(refined);
        Assert.InRange(refined!.WavenumberCm1, 2999.5, 3000.5);
    }

    [Fact]
    public void RefineManualPeak_NonWavenumberDataset_ReturnsNull()
    {
        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Absorbance",
            Points = SampleGaussian(200, 400, 1.0, 280, 8, 0.9),
        };

        var refined = IrPeakFinder.RefineManualPeak(dataset, clickedWavenumberCm1: 280);

        Assert.Null(refined);
    }

    [Fact]
    public void RefineManualPeak_TransmittanceIrScan_OperatesInAbsorbanceSpace()
    {
        var points = new List<SpectrumDataPoint>();
        for (var x = 400.0; x <= 4000.0001; x += 1.0)
        {
            var dip = 90.0 * Math.Exp(-Math.Pow(x - 1730, 2) / (2 * 64));
            points.Add(new SpectrumDataPoint { X = x, Y = 100.0 - dip });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "1/CM",
            RawYUnits = "TRANSMITTANCE",
            XLabel = "Wavenumber / cm⁻¹",
            YLabel = "Transmittance / %",
            Points = points,
        };

        var refined = IrPeakFinder.RefineManualPeak(dataset, clickedWavenumberCm1: 1740.0);

        Assert.NotNull(refined);
        Assert.InRange(refined!.WavenumberCm1, 1729.5, 1730.5);
    }

    [Fact]
    public void RefineManualPeak_NonFiniteClick_ReturnsNull()
    {
        var dataset = MakeIrAbsorbanceScan(SampleGaussian(400, 4000, 1.0, 1730, 8, 0.9));

        Assert.Null(IrPeakFinder.RefineManualPeak(dataset, double.NaN));
        Assert.Null(IrPeakFinder.RefineManualPeak(dataset, double.PositiveInfinity));
    }

    [Fact]
    public void RefineManualPeak_AgreesWithAutoDetectedPeak()
    {
        // A click placed exactly at the auto-detected wavenumber refines to
        // the same coordinate (within floating-point noise) so the manual
        // gesture cannot disagree with the automatic answer.
        var dataset = MakeIrAbsorbanceScan(SampleGaussian(400, 4000, 1.0, 1730, 8, 0.9));
        var auto = IrPeakFinder.Find(dataset, new IrPeakFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 1,
        });

        Assert.Single(auto);
        var refined = IrPeakFinder.RefineManualPeak(dataset, auto[0].WavenumberCm1);

        Assert.NotNull(refined);
        Assert.Equal(auto[0].WavenumberCm1, refined!.WavenumberCm1, precision: 6);
        Assert.Equal(auto[0].AbsorbanceValue, refined.AbsorbanceValue, precision: 6);
    }

    private static SpectrumDataset MakeIrAbsorbanceScan(List<SpectrumDataPoint> points)
    {
        return new SpectrumDataset
        {
            RawXUnits = "1/CM",
            RawYUnits = "ABSORBANCE",
            XLabel = "Wavenumber / cm⁻¹",
            YLabel = "Absorbance",
            Points = points,
        };
    }

    private static List<SpectrumDataPoint> SampleGaussian(
        double xMin, double xMax, double step,
        double center, double sigma, double height)
    {
        var result = new List<SpectrumDataPoint>();
        for (var x = xMin; x <= xMax + step / 2; x += step)
        {
            var y = height * Math.Exp(-Math.Pow(x - center, 2) / (2 * sigma * sigma));
            result.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        return result;
    }
}
