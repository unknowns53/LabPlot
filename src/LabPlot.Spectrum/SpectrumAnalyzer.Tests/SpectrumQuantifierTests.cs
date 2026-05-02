using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class SpectrumQuantifierTests
{
    [Fact]
    public void GetAbsorbanceAt_AbsorbanceDataset_LinearlyInterpolated()
    {
        // A = 0.1 at x=200, A = 1.0 at x=300 → A at 250 = 0.55
        var dataset = MakeAbsorbanceDataset(new[]
        {
            (200.0, 0.1),
            (300.0, 1.0),
        });

        var result = SpectrumQuantifier.GetAbsorbanceAt(dataset, 250.0);

        Assert.Equal(0.55, result, precision: 9);
    }

    [Fact]
    public void GetAbsorbanceAt_TransmittanceDataset_ConvertsToAbsorbance()
    {
        // T = 10 % at every x → A = -log10(0.1) = 1.0
        var dataset = MakeTransmittanceDataset(new[]
        {
            (200.0, 10.0),
            (300.0, 10.0),
        });

        var result = SpectrumQuantifier.GetAbsorbanceAt(dataset, 250.0);

        Assert.Equal(1.0, result, precision: 9);
    }

    [Fact]
    public void GetAbsorbanceAt_OutOfRange_ReturnsNaN()
    {
        var dataset = MakeAbsorbanceDataset(new[]
        {
            (200.0, 0.5),
            (300.0, 0.7),
        });

        Assert.True(double.IsNaN(SpectrumQuantifier.GetAbsorbanceAt(dataset, 100.0)));
        Assert.True(double.IsNaN(SpectrumQuantifier.GetAbsorbanceAt(dataset, 400.0)));
    }

    [Fact]
    public void GetAbsorbanceAt_ReflectanceDataset_ReturnsNaN()
    {
        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "REFLECTANCE",
            Points = MakePoints(new[] { (200.0, 0.5), (300.0, 0.7) }),
        };

        Assert.True(double.IsNaN(SpectrumQuantifier.GetAbsorbanceAt(dataset, 250.0)));
    }

    [Fact]
    public void GetIntegrationArea_ConstantSignal_ReturnsRectangleArea()
    {
        // A = 1 over [200, 300] with no baseline → area = 100
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 300.0001; x += 1.0)
        {
            points.Add(new SpectrumDataPoint { X = x, Y = 1.0 });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            Points = points,
        };

        var region = new IntegrationRegion
        {
            Label = "rect",
            XMin = 200,
            XMax = 300,
            BaselineMethod = BaselineMethod.None,
        };

        Assert.Equal(100.0, SpectrumQuantifier.GetIntegrationArea(dataset, region), precision: 9);
    }

    [Fact]
    public void Quantify_SingleWavelengthMode_UsesAbsorbanceAt()
    {
        var dataset = MakeAbsorbanceDataset(new[] { (200.0, 0.2), (400.0, 0.6) });
        var config = new CalibrationCurveConfig
        {
            Mode = CalibrationQuantificationMode.SingleWavelength,
            WavelengthNm = 300.0,
        };

        var result = SpectrumQuantifier.Quantify(dataset, config, Array.Empty<IntegrationRegion>());

        Assert.Equal(0.4, result, precision: 9);
    }

    [Fact]
    public void Quantify_IntegrationAreaMode_LooksUpRegionByLabel()
    {
        var points = new List<SpectrumDataPoint>();
        for (var x = 200.0; x <= 300.0001; x += 1.0)
        {
            points.Add(new SpectrumDataPoint { X = x, Y = 1.0 });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            Points = points,
        };

        var region = new IntegrationRegion
        {
            Label = "band1",
            XMin = 200,
            XMax = 300,
            BaselineMethod = BaselineMethod.None,
        };

        var config = new CalibrationCurveConfig
        {
            Mode = CalibrationQuantificationMode.IntegrationArea,
            IntegrationRegionLabel = "band1",
        };

        var result = SpectrumQuantifier.Quantify(dataset, config, new[] { region });

        Assert.Equal(100.0, result, precision: 9);
    }

    [Fact]
    public void Quantify_IntegrationAreaMode_UnknownLabel_ReturnsNaN()
    {
        var dataset = MakeAbsorbanceDataset(new[] { (200.0, 0.5), (300.0, 0.5) });
        var config = new CalibrationCurveConfig
        {
            Mode = CalibrationQuantificationMode.IntegrationArea,
            IntegrationRegionLabel = "missing",
        };

        var result = SpectrumQuantifier.Quantify(dataset, config, Array.Empty<IntegrationRegion>());

        Assert.True(double.IsNaN(result));
    }

    private static SpectrumDataset MakeAbsorbanceDataset(IEnumerable<(double X, double Y)> samples)
    {
        return new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            Points = MakePoints(samples),
        };
    }

    private static SpectrumDataset MakeTransmittanceDataset(IEnumerable<(double X, double Y)> samples)
    {
        return new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "TRANSMITTANCE",
            Points = MakePoints(samples),
        };
    }

    private static List<SpectrumDataPoint> MakePoints(IEnumerable<(double X, double Y)> samples)
    {
        var list = new List<SpectrumDataPoint>();
        foreach (var (x, y) in samples)
        {
            list.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        return list;
    }
}
