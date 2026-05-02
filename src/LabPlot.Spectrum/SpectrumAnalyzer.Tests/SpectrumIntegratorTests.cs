using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class SpectrumIntegratorTests
{
    [Fact]
    public void Integrate_ConstantSignal_NoBaseline_ReturnsRectangleArea()
    {
        // Y = 10 over X in [0, 10] → integral = 100
        var dataset = MakeDataset(SampleConstant(10.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "rect",
            XMin = 0,
            XMax = 10,
            BaselineMethod = BaselineMethod.None,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(100.0, result.Area, precision: 9);
        Assert.Equal(100.0, result.RawArea, precision: 9);
        Assert.Equal(0.0, result.BaselineArea, precision: 9);
        Assert.Equal(11, result.PointCount);
    }

    [Fact]
    public void Integrate_ConstantSignal_LinearBaseline_YieldsZero()
    {
        // Y = 10 with linear baseline through (0,10)–(10,10) → Area = 0
        var dataset = MakeDataset(SampleConstant(10.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "flat",
            XMin = 0,
            XMax = 10,
            BaselineMethod = BaselineMethod.Linear,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(0.0, result.Area, precision: 9);
        Assert.Equal(100.0, result.RawArea, precision: 9);
        Assert.Equal(100.0, result.BaselineArea, precision: 9);
    }

    [Fact]
    public void Integrate_LinearSignal_LinearBaseline_YieldsZero()
    {
        // Y = X over [0,10] → RawArea = 50, baseline through (0,0)-(10,10)
        // also integrates to 50, so corrected Area = 0.
        var dataset = MakeDataset(SampleLine(slope: 1.0, intercept: 0.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "ramp",
            XMin = 0,
            XMax = 10,
            BaselineMethod = BaselineMethod.Linear,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(0.0, result.Area, precision: 9);
        Assert.Equal(50.0, result.RawArea, precision: 9);
        Assert.Equal(50.0, result.BaselineArea, precision: 9);
    }

    [Fact]
    public void Integrate_PeakOnSlopingBaseline_LinearBaseline_RecoversTriangleArea()
    {
        // Y = (X / 10) + triangular peak (height 5 at X=5, base from 4 to 6).
        // Linear baseline removes the slope, leaving the triangle's area = 5.
        var points = new List<SpectrumDataPoint>();
        for (var x = 0.0; x <= 10.0001; x += 0.1)
        {
            var slope = x / 10.0;
            var peak = x switch
            {
                >= 4.0 and <= 5.0 => (x - 4.0) * 5.0,   // up to 5 at x=5
                > 5.0 and <= 6.0 => (6.0 - x) * 5.0,    // back to 0 at x=6
                _ => 0.0,
            };
            points.Add(new SpectrumDataPoint { X = Math.Round(x, 5), Y = slope + peak });
        }

        var dataset = MakeDataset(points);
        var region = new IntegrationRegion
        {
            Label = "peak",
            XMin = 4.0,
            XMax = 6.0,
            BaselineMethod = BaselineMethod.Linear,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        // Triangle area = 0.5 * base * height = 0.5 * 2 * 5 = 5.
        Assert.True(result.HasResult);
        Assert.Equal(5.0, result.Area, precision: 2);
    }

    [Fact]
    public void Integrate_RegionOutsideDatasetRange_ReturnsEmpty()
    {
        var dataset = MakeDataset(SampleConstant(1.0, 0, 10, 11));
        var region = new IntegrationRegion { Label = "oob", XMin = 20, XMax = 30 };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.False(result.HasResult);
        Assert.Equal(0, result.PointCount);
        Assert.True(double.IsNaN(result.Area));
    }

    [Fact]
    public void Integrate_RegionPartiallyOutside_ReturnsEmpty()
    {
        // Current spec: any over-hang yields empty so the user re-bounds the region.
        var dataset = MakeDataset(SampleConstant(1.0, 0, 10, 11));
        var region = new IntegrationRegion { Label = "edge", XMin = -1, XMax = 5 };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Integrate_BoundariesBetweenSamples_InterpolatesEnds()
    {
        // Constant Y=2 sampled at 0..10 (step 1). Region [0.5, 9.5] should
        // still integrate to 2 * 9 = 18.
        var dataset = MakeDataset(SampleConstant(2.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "interp",
            XMin = 0.5,
            XMax = 9.5,
            BaselineMethod = BaselineMethod.None,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(18.0, result.RawArea, precision: 9);
    }

    [Fact]
    public void IntegrationRegion_IsValid_RejectsEmptyLabelOrInvertedRange()
    {
        Assert.False(new IntegrationRegion { Label = "", XMin = 0, XMax = 1 }.IsValid);
        Assert.False(new IntegrationRegion { Label = "ok", XMin = 1, XMax = 0 }.IsValid);
        Assert.False(new IntegrationRegion { Label = "ok", XMin = 1, XMax = 1 }.IsValid);
        Assert.False(new IntegrationRegion { Label = "ok", XMin = double.NaN, XMax = 1 }.IsValid);
        Assert.True(new IntegrationRegion { Label = "ok", XMin = 0, XMax = 1 }.IsValid);
    }

    [Fact]
    public void IntegrationRegion_IsValid_RejectsBadParameters()
    {
        // RubberBandSegments below 2 only matters when the method is RubberBand.
        Assert.False(new IntegrationRegion
        {
            Label = "rb", XMin = 0, XMax = 1,
            BaselineMethod = BaselineMethod.RubberBand, RubberBandSegments = 1,
        }.IsValid);
        Assert.True(new IntegrationRegion
        {
            Label = "rb", XMin = 0, XMax = 1,
            BaselineMethod = BaselineMethod.Linear, RubberBandSegments = 1,
        }.IsValid);

        // PolynomialOrder must be in [1, 5] when the method is Polynomial.
        Assert.False(new IntegrationRegion
        {
            Label = "p", XMin = 0, XMax = 1,
            BaselineMethod = BaselineMethod.Polynomial, PolynomialOrder = 0,
        }.IsValid);
        Assert.False(new IntegrationRegion
        {
            Label = "p", XMin = 0, XMax = 1,
            BaselineMethod = BaselineMethod.Polynomial, PolynomialOrder = 6,
        }.IsValid);
        Assert.True(new IntegrationRegion
        {
            Label = "p", XMin = 0, XMax = 1,
            BaselineMethod = BaselineMethod.Polynomial, PolynomialOrder = 3,
        }.IsValid);
        Assert.True(new IntegrationRegion
        {
            Label = "p", XMin = 0, XMax = 1,
            BaselineMethod = BaselineMethod.None, PolynomialOrder = 0,
        }.IsValid);
    }

    [Fact]
    public void Integrate_ConstantSignal_ConvexHullBaseline_YieldsZero()
    {
        var dataset = MakeDataset(SampleConstant(10.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "flat",
            XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.ConvexHull,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(0.0, result.Area, precision: 9);
        Assert.Equal(100.0, result.RawArea, precision: 9);
    }

    [Fact]
    public void Integrate_LinearSignal_ConvexHullBaseline_YieldsZero()
    {
        var dataset = MakeDataset(SampleLine(slope: 1.0, intercept: 0.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "ramp",
            XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.ConvexHull,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(0.0, result.Area, precision: 9);
    }

    [Fact]
    public void Integrate_PeakOnSlopingBaseline_ConvexHullBaseline_RecoversTriangleArea()
    {
        var points = SamplePeakOnSlope();
        var dataset = MakeDataset(points);
        var region = new IntegrationRegion
        {
            Label = "peak",
            XMin = 4.0, XMax = 6.0,
            BaselineMethod = BaselineMethod.ConvexHull,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(5.0, result.Area, precision: 1);
    }

    [Fact]
    public void Integrate_ConstantSignal_RubberBandBaseline_YieldsZero()
    {
        var dataset = MakeDataset(SampleConstant(10.0, 0, 10, 21));
        var region = new IntegrationRegion
        {
            Label = "flat",
            XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.RubberBand,
            RubberBandSegments = 8,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(0.0, result.Area, precision: 9);
    }

    [Fact]
    public void Integrate_PeakOnSlopingBaseline_RubberBandBaseline_RecoversTriangleArea()
    {
        // For a piecewise-linear rubber-band each segment must straddle a
        // baseline-only stretch of the spectrum so its minimum lands off
        // the peak. Region [3, 7] (width 4) with Segments=2 puts the peak
        // (width 2) entirely inside one half-segment for each side, so the
        // segment minima are on the linear baseline.
        var points = SamplePeakOnSlope();
        var dataset = MakeDataset(points);
        var region = new IntegrationRegion
        {
            Label = "peak",
            XMin = 3.0, XMax = 7.0,
            BaselineMethod = BaselineMethod.RubberBand,
            RubberBandSegments = 2,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(5.0, result.Area, precision: 1);
    }

    [Fact]
    public void Integrate_PeakOnSlopingBaseline_RubberBandHullBaseline_RecoversTriangleArea()
    {
        // The hull variant clips off any segment minimum that would lift the
        // baseline onto the peak, so it tolerates Segments choices that are
        // too coarse for the plain rubber-band — here Segments=16 across a
        // peak that fills [4, 6] still lands on the linear baseline.
        var points = SamplePeakOnSlope();
        var dataset = MakeDataset(points);
        var region = new IntegrationRegion
        {
            Label = "peak",
            XMin = 4.0, XMax = 6.0,
            BaselineMethod = BaselineMethod.RubberBandHull,
            RubberBandSegments = 16,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(5.0, result.Area, precision: 1);
    }

    [Fact]
    public void Integrate_RubberBandSegments_AffectsBaseline()
    {
        // Segments=2 forces the rubber-band to find one minimum per half of
        // the region — chord-like. Segments=64 lets it track the parabolic
        // baseline far more closely. The two BaselineArea values must therefore
        // differ; if they coincided the user knob would be doing nothing.
        var points = new List<SpectrumDataPoint>();
        for (var x = 0.0; x <= 10.0001; x += 0.05)
        {
            points.Add(new SpectrumDataPoint
            {
                X = Math.Round(x, 5),
                Y = 0.1 * (x - 5.0) * (x - 5.0),
            });
        }

        var dataset = MakeDataset(points);
        var coarse = new IntegrationRegion
        {
            Label = "rb-coarse", XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.RubberBand,
            RubberBandSegments = 2,
        };
        var fine = coarse with { Label = "rb-fine", RubberBandSegments = 64 };

        var coarseResult = SpectrumIntegrator.Integrate(dataset, coarse);
        var fineResult = SpectrumIntegrator.Integrate(dataset, fine);

        Assert.True(coarseResult.HasResult);
        Assert.True(fineResult.HasResult);
        Assert.NotEqual(coarseResult.BaselineArea, fineResult.BaselineArea);
        // For a purely-baseline (no peak) parabola, more segments → tighter
        // tracking → smaller residual area.
        Assert.True(Math.Abs(fineResult.Area) < Math.Abs(coarseResult.Area));
    }

    [Fact]
    public void Integrate_ConstantSignal_PolynomialBaseline_YieldsZero()
    {
        // Constant dataset: the convex hull is just the two endpoints, so
        // Polynomial(2) falls back to a linear fit through those two points
        // — which still yields a zero area for a flat signal.
        var dataset = MakeDataset(SampleConstant(10.0, 0, 10, 21));
        var region = new IntegrationRegion
        {
            Label = "flat",
            XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.Polynomial,
            PolynomialOrder = 1,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.Equal(0.0, result.Area, precision: 6);
    }

    [Fact]
    public void Integrate_QuadraticSignal_PolynomialOrder2_YieldsZero()
    {
        // Y = (x - 5)^2 over [0, 10]: a parabola the order-2 polynomial
        // baseline should fit exactly, leaving zero area.
        var points = new List<SpectrumDataPoint>();
        for (var x = 0.0; x <= 10.0001; x += 0.1)
        {
            points.Add(new SpectrumDataPoint { X = Math.Round(x, 5), Y = (x - 5.0) * (x - 5.0) });
        }

        var dataset = MakeDataset(points);
        var region = new IntegrationRegion
        {
            Label = "parabola",
            XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.Polynomial,
            PolynomialOrder = 2,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(0.0, result.Area, precision: 4);
    }

    [Fact]
    public void Integrate_PeakOnQuadraticBaseline_PolynomialOrder2_RecoversTriangleArea()
    {
        // Y = 0.1 * (x - 5)^2 + triangular peak (height 5 at x=5, base 4..6).
        // Integration window deliberately overhangs the peak (XMin=3, XMax=7)
        // so the convex hull captures a few baseline samples on either side of
        // the peak — without that overhang the polynomial fit has nothing
        // curved to anchor onto and degenerates to a chord.
        var points = new List<SpectrumDataPoint>();
        for (var x = 0.0; x <= 10.0001; x += 0.05)
        {
            var baseline = 0.1 * (x - 5.0) * (x - 5.0);
            var peak = x switch
            {
                >= 4.0 and <= 5.0 => (x - 4.0) * 5.0,
                > 5.0 and <= 6.0 => (6.0 - x) * 5.0,
                _ => 0.0,
            };
            points.Add(new SpectrumDataPoint { X = Math.Round(x, 5), Y = baseline + peak });
        }

        var dataset = MakeDataset(points);
        var region = new IntegrationRegion
        {
            Label = "peak",
            XMin = 3.0, XMax = 7.0,
            BaselineMethod = BaselineMethod.Polynomial,
            PolynomialOrder = 2,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(5.0, result.Area, precision: 2);
    }

    [Fact]
    public void Integrate_PolynomialOrderTooHighForHull_FallsBackToLinear()
    {
        // Three samples can produce at most three hull vertices, so order 5
        // (which needs 6 anchors) must fall back to the linear baseline.
        var dataset = MakeDataset(SampleConstant(2.0, 0, 10, 3));
        var highOrderRegion = new IntegrationRegion
        {
            Label = "hi",
            XMin = 0, XMax = 10,
            BaselineMethod = BaselineMethod.Polynomial,
            PolynomialOrder = 5,
        };
        var linearReference = highOrderRegion with { BaselineMethod = BaselineMethod.Linear };

        var highOrderResult = SpectrumIntegrator.Integrate(dataset, highOrderRegion);
        var linearResult = SpectrumIntegrator.Integrate(dataset, linearReference);

        Assert.True(highOrderResult.HasResult);
        Assert.Equal(linearResult.BaselineArea, highOrderResult.BaselineArea, precision: 9);
        Assert.Equal(linearResult.Area, highOrderResult.Area, precision: 9);
    }

    private static List<SpectrumDataPoint> SamplePeakOnSlope()
    {
        // Y = (x / 10) baseline + triangular peak (height 5 at x=5, base 4..6).
        var points = new List<SpectrumDataPoint>();
        for (var x = 0.0; x <= 10.0001; x += 0.05)
        {
            var slope = x / 10.0;
            var peak = x switch
            {
                >= 4.0 and <= 5.0 => (x - 4.0) * 5.0,
                > 5.0 and <= 6.0 => (6.0 - x) * 5.0,
                _ => 0.0,
            };
            points.Add(new SpectrumDataPoint { X = Math.Round(x, 5), Y = slope + peak });
        }

        return points;
    }

    [Fact]
    public void IntegrationResult_HasResult_RequiresAtLeastTwoPoints()
    {
        var region = new IntegrationRegion { Label = "r", XMin = 0, XMax = 1 };

        Assert.False(new IntegrationResult
        {
            Region = region,
            Area = 0,
            RawArea = 0,
            BaselineArea = 0,
            PointCount = 1,
        }.HasResult);

        Assert.True(new IntegrationResult
        {
            Region = region,
            Area = 0,
            RawArea = 0,
            BaselineArea = 0,
            PointCount = 2,
        }.HasResult);
    }

    [Fact]
    public void Integrate_InvalidRegion_ReturnsEmpty()
    {
        var dataset = MakeDataset(SampleConstant(1.0, 0, 10, 11));
        var region = new IntegrationRegion { Label = "bad", XMin = 5, XMax = 5 };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Integrate_TransmittanceDataset_IntegratesInAbsorbanceSpace()
    {
        // T = 10 % is constant → A = -log10(0.10) = 1.0. Integrated over X in [0, 10]
        // the area should be 1.0 * 10 = 10. Without the Absorbance conversion the
        // raw integral would be 10 % * 10 = 100.
        var dataset = MakeTransmittanceDataset(SampleConstant(10.0, 0, 10, 11));
        var region = new IntegrationRegion
        {
            Label = "abs",
            XMin = 0,
            XMax = 10,
            BaselineMethod = BaselineMethod.None,
        };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.True(result.HasResult);
        Assert.Equal(10.0, result.RawArea, precision: 9);
    }

    [Fact]
    public void Integrate_NonAbsorbanceCompatibleDataset_ReturnsEmpty()
    {
        // RawYUnits that is not A or T (here Reflectance) cannot be expressed as
        // Absorbance, so integration is refused.
        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "REFLECTANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Reflectance",
            Points = SampleConstant(0.5, 0, 10, 11),
        };
        var region = new IntegrationRegion { Label = "r", XMin = 0, XMax = 10 };

        var result = SpectrumIntegrator.Integrate(dataset, region);

        Assert.False(result.HasResult);
    }

    private static SpectrumDataset MakeTransmittanceDataset(List<SpectrumDataPoint> points)
    {
        return new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "TRANSMITTANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Transmittance / %",
            Points = points,
        };
    }

    private static SpectrumDataset MakeDataset(List<SpectrumDataPoint> points)
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

    private static List<SpectrumDataPoint> SampleConstant(double y, double xMin, double xMax, int count)
    {
        var step = (xMax - xMin) / (count - 1);
        var result = new List<SpectrumDataPoint>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(new SpectrumDataPoint { X = xMin + i * step, Y = y });
        }

        return result;
    }

    private static List<SpectrumDataPoint> SampleLine(double slope, double intercept, double xMin, double xMax, int count)
    {
        var step = (xMax - xMin) / (count - 1);
        var result = new List<SpectrumDataPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var x = xMin + i * step;
            result.Add(new SpectrumDataPoint { X = x, Y = slope * x + intercept });
        }

        return result;
    }
}
