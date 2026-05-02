using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class LambdaMaxFinderTests
{
    [Fact]
    public void Find_SingleGaussianPeak_RecoversCenterByParabolicInterpolation()
    {
        // Gaussian centred at 280 nm, sigma 8, peak height 0.9.
        var dataset = MakeWavelengthScan(SampleGaussian(200, 400, 0.5, 280, 8, 0.9));

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavelengthNm, 279.5, 280.5);
        Assert.InRange(peaks[0].AbsorbanceValue, 0.85, 0.95);
    }

    [Fact]
    public void Find_TwoPeaks_RanksByAbsorbance()
    {
        // Two Gaussians: tall at 280 (height 1.0), short at 350 (height 0.4).
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 500.0001; x += 1.0)
        {
            var y = 1.0 * Math.Exp(-Math.Pow(x - 280, 2) / (2 * 64))
                  + 0.4 * Math.Exp(-Math.Pow(x - 350, 2) / (2 * 100));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeWavelengthScan(points);

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Equal(2, peaks.Count);
        Assert.InRange(peaks[0].WavelengthNm, 279.5, 280.5);
        Assert.InRange(peaks[1].WavelengthNm, 349.5, 350.5);
        Assert.True(peaks[0].AbsorbanceValue > peaks[1].AbsorbanceValue);
    }

    [Fact]
    public void Find_RespectsMinimumAbsorbanceFilter()
    {
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 500.0001; x += 1.0)
        {
            var y = 0.05 * Math.Exp(-Math.Pow(x - 280, 2) / (2 * 64))
                  + 0.5 * Math.Exp(-Math.Pow(x - 350, 2) / (2 * 100));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeWavelengthScan(points);

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.1, // hides the small 280 peak
            Window = 3,
            MaxPeaks = 5,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavelengthNm, 349.5, 350.5);
    }

    [Fact]
    public void Find_RespectsMaxPeaksLimit()
    {
        // Three explicit peaks at 250, 300, 400 with descending heights.
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 500.0001; x += 1.0)
        {
            var y = 1.0 * Math.Exp(-Math.Pow(x - 250, 2) / (2 * 25))
                  + 0.7 * Math.Exp(-Math.Pow(x - 300, 2) / (2 * 25))
                  + 0.4 * Math.Exp(-Math.Pow(x - 400, 2) / (2 * 25));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeWavelengthScan(points);

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 2,
        });

        Assert.Equal(2, peaks.Count);
        Assert.InRange(peaks[0].WavelengthNm, 249.5, 250.5);
        Assert.InRange(peaks[1].WavelengthNm, 299.5, 300.5);
    }

    [Fact]
    public void Find_RestrictsToWavelengthRange()
    {
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 500.0001; x += 1.0)
        {
            var y = 1.0 * Math.Exp(-Math.Pow(x - 250, 2) / (2 * 25))
                  + 0.5 * Math.Exp(-Math.Pow(x - 400, 2) / (2 * 25));
            points.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        var dataset = MakeWavelengthScan(points);

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 5,
            WavelengthMinNm = 350,
            WavelengthMaxNm = 450,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavelengthNm, 399.5, 400.5);
    }

    [Fact]
    public void Find_NonWavelengthDataset_ReturnsEmpty()
    {
        var dataset = new SpectrumDataset
        {
            RawXUnits = "Temperature[C]",
            RawYUnits = "ABSORBANCE",
            XLabel = "Temperature / °C",
            YLabel = "Absorbance",
            Points = SampleGaussian(20, 60, 0.5, 35, 1, 0.5),
        };

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig());

        Assert.Empty(peaks);
    }

    [Fact]
    public void Find_TransmittanceWavelengthScan_ConvertsToAbsorbance()
    {
        // Transmittance dip at 280 nm means an absorbance peak at 280 nm.
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 400.0001; x += 1.0)
        {
            // T = 100 - 90 * Gaussian → varies between ~10 % at peak and 100 % at tails.
            var dip = 90.0 * Math.Exp(-Math.Pow(x - 280, 2) / (2 * 64));
            points.Add(new SpectrumDataPoint { X = x, Y = 100.0 - dip });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "TRANSMITTANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Transmittance / %",
            Points = points,
        };

        var peaks = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.1,
            Window = 3,
            MaxPeaks = 1,
        });

        Assert.Single(peaks);
        Assert.InRange(peaks[0].WavelengthNm, 279.5, 280.5);
    }

    [Fact]
    public void Result_HasResult_TrueForFiniteValues()
    {
        var ok = new LambdaMaxResult { WavelengthNm = 280, AbsorbanceValue = 0.5, SampleIndex = 100 };
        var nan = new LambdaMaxResult { WavelengthNm = double.NaN, AbsorbanceValue = 0.5, SampleIndex = 0 };

        Assert.True(ok.HasResult);
        Assert.False(nan.HasResult);
    }

    [Fact]
    public void RefineManualPeak_ClickInsideSnapWindow_SnapsToLocalMaximum()
    {
        // Gaussian centred at 280 nm. Click at 277 nm (inside the ±5 nm snap
        // window) should land back on 280.
        var dataset = MakeWavelengthScan(SampleGaussian(200, 400, 0.5, 280, 8, 0.9));

        var refined = LambdaMaxFinder.RefineManualPeak(dataset, clickedWavelengthNm: 277.0);

        Assert.NotNull(refined);
        Assert.InRange(refined!.WavelengthNm, 279.5, 280.5);
        Assert.InRange(refined.AbsorbanceValue, 0.85, 0.95);
    }

    [Fact]
    public void RefineManualPeak_ClickOutsideSnapWindow_FallsBackToNearestPoint()
    {
        // No peak near 350 nm (Gaussian centre is 280). Click at 350 with
        // a tight 1 nm snap window: empty window → nearest-neighbour fallback
        // returns the data point closest to the click.
        var dataset = MakeWavelengthScan(SampleGaussian(200, 400, 1.0, 280, 8, 0.9));

        var refined = LambdaMaxFinder.RefineManualPeak(dataset, clickedWavelengthNm: 350.4, snapWindowNm: 0.05);

        Assert.NotNull(refined);
        // Snap to the X grid (1 nm steps), so result is 350 nm.
        Assert.InRange(refined!.WavelengthNm, 349.5, 350.5);
    }

    [Fact]
    public void RefineManualPeak_NonWavelengthDataset_ReturnsNull()
    {
        var dataset = new SpectrumDataset
        {
            RawXUnits = "Temperature[C]",
            RawYUnits = "ABSORBANCE",
            XLabel = "Temperature / °C",
            YLabel = "Absorbance",
            Points = SampleGaussian(20, 60, 0.5, 35, 1, 0.5),
        };

        var refined = LambdaMaxFinder.RefineManualPeak(dataset, clickedWavelengthNm: 35);

        Assert.Null(refined);
    }

    [Fact]
    public void RefineManualPeak_TransmittanceWavelengthScan_OperatesInAbsorbanceSpace()
    {
        // Same transmittance dip as the auto-detect test: clicking near
        // 280 nm should snap to the absorbance peak (= transmittance dip).
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 400.0001; x += 1.0)
        {
            var dip = 90.0 * Math.Exp(-Math.Pow(x - 280, 2) / (2 * 64));
            points.Add(new SpectrumDataPoint { X = x, Y = 100.0 - dip });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "TRANSMITTANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Transmittance / %",
            Points = points,
        };

        var refined = LambdaMaxFinder.RefineManualPeak(dataset, clickedWavelengthNm: 282.0);

        Assert.NotNull(refined);
        Assert.InRange(refined!.WavelengthNm, 279.5, 280.5);
    }

    [Fact]
    public void RefineManualPeak_NonFiniteClick_ReturnsNull()
    {
        var dataset = MakeWavelengthScan(SampleGaussian(200, 400, 1.0, 280, 8, 0.9));

        Assert.Null(LambdaMaxFinder.RefineManualPeak(dataset, double.NaN));
        Assert.Null(LambdaMaxFinder.RefineManualPeak(dataset, double.PositiveInfinity));
    }

    [Fact]
    public void RefineManualPeak_AgreesWithAutoDetectedPeak()
    {
        // A click placed exactly at the auto-detected wavelength should
        // refine to the same coordinate (within floating-point noise) so
        // the manual gesture cannot disagree with the automatic answer.
        var dataset = MakeWavelengthScan(SampleGaussian(200, 400, 1.0, 280, 8, 0.9));
        var auto = LambdaMaxFinder.Find(dataset, new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = 0.05,
            Window = 3,
            MaxPeaks = 1,
        });

        Assert.Single(auto);
        var refined = LambdaMaxFinder.RefineManualPeak(dataset, auto[0].WavelengthNm);

        Assert.NotNull(refined);
        Assert.Equal(auto[0].WavelengthNm, refined!.WavelengthNm, precision: 6);
        Assert.Equal(auto[0].AbsorbanceValue, refined.AbsorbanceValue, precision: 6);
    }

    private static SpectrumDataset MakeWavelengthScan(List<SpectrumDataPoint> points)
    {
        return new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            XLabel = "Wavelength / nm",
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
