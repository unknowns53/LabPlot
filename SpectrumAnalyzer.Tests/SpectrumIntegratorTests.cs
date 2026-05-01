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
