using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class CloudPointDetectorTests
{
    [Fact]
    public void Detect_Midpoint_FindsTransmittance50CrossingByLinearInterpolation()
    {
        // Linear ramp from T=100 % at 30 °C down to T=0 % at 50 °C.
        // 50 % crossing should land exactly at 40 °C.
        var dataset = MakeTemperatureScan(SampleLinear(30, 100, 50, 0, 21), firstX: 30, lastX: 50);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            TransmittanceThresholdPercent = 50.0,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.True(result.HasResult);
        Assert.Equal(CloudPointMethod.Midpoint, result.Method);
        Assert.Equal(40.0, result.TemperatureCelsius, precision: 6);
        Assert.Equal(50.0, result.TransmittancePercentAtTc, precision: 6);
        Assert.Equal(ScanDirection.Heating, result.Direction);
    }

    [Fact]
    public void Detect_Midpoint_RespectsCustomThreshold()
    {
        // Same ramp; threshold = 80 % should hit at 30 + 20% of 20 = 34 °C.
        var dataset = MakeTemperatureScan(SampleLinear(30, 100, 50, 0, 21), firstX: 30, lastX: 50);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            TransmittanceThresholdPercent = 80.0,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.Equal(34.0, result.TemperatureCelsius, precision: 6);
    }

    [Fact]
    public void Detect_FirstDerivativePeak_PicksSteepestPoint()
    {
        // Sigmoid: T(°C) = 100 / (1 + exp((Temp - 35)/0.5)).
        // Steepest slope is at Temp = 35. Sample finely so the centred
        // difference can locate it.
        var points = new List<SpectrumDataPoint>();
        for (var x = 25.0; x <= 45.0001; x += 0.25)
        {
            var t = 100.0 / (1.0 + Math.Exp((x - 35.0) / 0.5));
            points.Add(new SpectrumDataPoint { X = x, Y = t });
        }

        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.FirstDerivativePeak,
            SmoothingWindow = 3,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.True(result.HasResult);
        Assert.Equal(CloudPointMethod.FirstDerivativePeak, result.Method);
        Assert.InRange(result.TemperatureCelsius, 34.5, 35.5);
    }

    [Fact]
    public void Detect_NonTemperatureScan_ReturnsEmpty()
    {
        var dataset = new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = "ABSORBANCE",
            XLabel = "Wavelength / nm",
            YLabel = "Absorbance",
            Points = SampleLinear(200, 1.0, 800, 0.0, 11),
        };

        var result = CloudPointDetector.Detect(dataset, new CloudPointDetectionConfig());

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Detect_AbsorbanceTemperatureScan_ConvertsToTransmittance()
    {
        // Absorbance ramp: A goes from 0 (T=100 %) to 2 (T=1 %) over 30..50.
        // The 50 % crossing in T corresponds to A = 0.301...
        var points = new List<SpectrumDataPoint>();
        for (var i = 0; i <= 20; i++)
        {
            var x = 30.0 + i;
            var a = 2.0 * (i / 20.0);
            points.Add(new SpectrumDataPoint { X = x, Y = a });
        }

        var dataset = new SpectrumDataset
        {
            RawXUnits = "Temperature[C]",
            RawYUnits = "ABSORBANCE",
            XLabel = "Temperature / °C",
            YLabel = "Absorbance",
            RawFirstX = 30,
            RawLastX = 50,
            Points = points,
        };

        var result = CloudPointDetector.Detect(dataset, new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            TransmittanceThresholdPercent = 50.0,
        });

        Assert.True(result.HasResult);
        // 50 % T == A = 0.301; with 0..2 over 20 °C, 0.301 is at 0.301/2 * 20 + 30 = 33.01 °C.
        Assert.InRange(result.TemperatureCelsius, 32.9, 33.2);
    }

    [Fact]
    public void Detect_PreservesDirectionFromHeader()
    {
        var heating = MakeTemperatureScan(SampleLinear(30, 100, 50, 0, 21), firstX: 30, lastX: 50);
        var cooling = MakeTemperatureScan(SampleLinear(30, 0, 50, 100, 21), firstX: 50, lastX: 30);

        var heatRes = CloudPointDetector.Detect(heating, new CloudPointDetectionConfig());
        var coolRes = CloudPointDetector.Detect(cooling, new CloudPointDetectionConfig());

        Assert.Equal(ScanDirection.Heating, heatRes.Direction);
        Assert.Equal(ScanDirection.Cooling, coolRes.Direction);
    }

    [Fact]
    public void Detect_NoCrossing_ReturnsEmpty()
    {
        // T stays above 70 % the whole sweep — no 50 % crossing.
        var dataset = MakeTemperatureScan(SampleLinear(30, 90, 50, 75, 11), firstX: 30, lastX: 50);

        var result = CloudPointDetector.Detect(dataset, new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            TransmittanceThresholdPercent = 50.0,
        });

        Assert.False(result.HasResult);
    }

    [Fact]
    public void HysteresisAnalyzer_ReturnsCoolingMinusHeating()
    {
        var heating = new CloudPointResult
        {
            Method = CloudPointMethod.Midpoint,
            TemperatureCelsius = 32.0,
            TransmittancePercentAtTc = 50.0,
            Direction = ScanDirection.Heating,
        };
        var cooling = new CloudPointResult
        {
            Method = CloudPointMethod.Midpoint,
            TemperatureCelsius = 30.5,
            TransmittancePercentAtTc = 50.0,
            Direction = ScanDirection.Cooling,
        };

        var delta = HysteresisAnalyzer.ComputeHysteresis(heating, cooling);

        Assert.Equal(-1.5, delta, precision: 6);
    }

    [Fact]
    public void HysteresisAnalyzer_ReturnsNaNWhenAnyMissing()
    {
        var heating = new CloudPointResult
        {
            Method = CloudPointMethod.Midpoint,
            TemperatureCelsius = 32.0,
            TransmittancePercentAtTc = 50.0,
            Direction = ScanDirection.Heating,
        };

        Assert.True(double.IsNaN(HysteresisAnalyzer.ComputeHysteresis(heating, null)));
        Assert.True(double.IsNaN(HysteresisAnalyzer.ComputeHysteresis(null, heating)));
        Assert.True(double.IsNaN(HysteresisAnalyzer.ComputeHysteresis(null, null)));
    }

    [Fact]
    public void OriginalScanDirection_HeatingWhenLastXGreater()
    {
        var heating = MakeTemperatureScan(SampleLinear(30, 100, 50, 0, 21), firstX: 30, lastX: 50);
        var cooling = MakeTemperatureScan(SampleLinear(30, 0, 50, 100, 21), firstX: 50, lastX: 30);
        var unknown = MakeTemperatureScan(SampleLinear(30, 100, 50, 0, 21), firstX: null, lastX: null);

        Assert.Equal(ScanDirection.Heating, heating.OriginalScanDirection);
        Assert.Equal(ScanDirection.Cooling, cooling.OriginalScanDirection);
        Assert.Equal(ScanDirection.Unknown, unknown.OriginalScanDirection);
    }

    private static SpectrumDataset MakeTemperatureScan(
        List<SpectrumDataPoint> points,
        double? firstX,
        double? lastX)
    {
        return new SpectrumDataset
        {
            RawXUnits = "Temperature[C]",
            RawYUnits = "TRANSMITTANCE",
            XLabel = "Temperature / °C",
            YLabel = "Transmittance / %",
            RawFirstX = firstX,
            RawLastX = lastX,
            Points = points,
        };
    }

    private static List<SpectrumDataPoint> SampleLinear(double x0, double y0, double x1, double y1, int count)
    {
        var result = new List<SpectrumDataPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / (count - 1);
            result.Add(new SpectrumDataPoint
            {
                X = x0 + t * (x1 - x0),
                Y = y0 + t * (y1 - y0),
            });
        }

        // Reader sorts ascending; mimic that.
        result.Sort((a, b) => a.X.CompareTo(b.X));
        return result;
    }
}
