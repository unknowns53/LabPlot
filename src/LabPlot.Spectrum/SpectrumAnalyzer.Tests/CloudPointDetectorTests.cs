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
    public void Detect_SecondDerivativeExtremum_PicksOnsetForHeating()
    {
        // Sigmoid: T(°C) = 100 / (1 + exp((Temp - 35)/2)). For a heating
        // sweep the onset (curvature peak on the pre-transition baseline
        // side) lies analytically at Temp ≈ 31.5 — below the inflection at
        // 35 °C.
        var points = SampleSigmoid(start: 25.0, stop: 45.0, step: 0.25, center: 35.0, k: 2.0);
        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.SecondDerivativeExtremum,
            SmoothingWindow = 3,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.True(result.HasResult);
        Assert.Equal(CloudPointMethod.SecondDerivativeExtremum, result.Method);
        Assert.Equal(ScanDirection.Heating, result.Direction);
        Assert.InRange(result.TemperatureCelsius, 30.5, 32.5);
    }

    [Fact]
    public void Detect_SecondDerivativeExtremum_PicksOnsetForCooling()
    {
        // Same sigmoid, recorded with cooling direction (firstX > lastX).
        // The "baseline" side of the original sweep is at high T after
        // ascending sort, so the onset should land near 38.5 °C.
        var points = SampleSigmoid(start: 25.0, stop: 45.0, step: 0.25, center: 35.0, k: 2.0);
        var dataset = MakeTemperatureScan(points, firstX: 45, lastX: 25);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.SecondDerivativeExtremum,
            SmoothingWindow = 3,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.True(result.HasResult);
        Assert.Equal(ScanDirection.Cooling, result.Direction);
        Assert.InRange(result.TemperatureCelsius, 37.5, 39.5);
    }

    [Fact]
    public void Detect_SecondDerivativeExtremum_OnsetIsStrictlyBeforeInflection()
    {
        // Sanity check: for a heating sigmoid the onset method must return a
        // temperature strictly below the inflection picked by the
        // first-derivative-peak method, otherwise the two UI options are
        // indistinguishable.
        var points = SampleSigmoid(start: 25.0, stop: 45.0, step: 0.25, center: 35.0, k: 2.0);
        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);

        var inflection = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.FirstDerivativePeak });
        var onset = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SecondDerivativeExtremum });

        Assert.True(inflection.HasResult);
        Assert.True(onset.HasResult);
        Assert.True(
            onset.TemperatureCelsius < inflection.TemperatureCelsius,
            $"Onset {onset.TemperatureCelsius} should be below inflection {inflection.TemperatureCelsius}");
    }

    [Fact]
    public void Detect_SecondDerivativeExtremum_LinearRampReturnsEmpty()
    {
        // A pure linear ramp has zero curvature everywhere, so the onset
        // method has nothing to lock onto.
        var dataset = MakeTemperatureScan(SampleLinear(30, 100, 50, 0, 21), firstX: 30, lastX: 50);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.SecondDerivativeExtremum,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Detect_SigmoidFit_RecoversBoltzmannParameters()
    {
        // PNIPAM-like heating sweep: T drops from ~95 % to ~5 % as the sample
        // crosses Tc. With our Boltzmann parametrisation that means k > 0
        // (descending curve) — Tc = 33.5 °C, k = +1.5 °C. The fitter should
        // recover all four parameters to within ~0.1 of their true values.
        const double tLowTrue = 5.0;
        const double tHighTrue = 95.0;
        const double tcTrue = 33.5;
        const double kTrue = 1.5;

        var points = SampleBoltzmann(25, 45, 0.25, tLowTrue, tHighTrue, tcTrue, kTrue);
        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var result = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SigmoidFit });

        Assert.True(result.HasResult);
        Assert.Equal(CloudPointMethod.SigmoidFit, result.Method);
        Assert.Equal(tcTrue, result.TemperatureCelsius, precision: 1);
        Assert.NotNull(result.TLowPercent);
        Assert.NotNull(result.THighPercent);
        Assert.NotNull(result.KSlopeCelsius);
        Assert.NotNull(result.RSquared);
        Assert.Equal(tLowTrue, result.TLowPercent!.Value, precision: 1);
        Assert.Equal(tHighTrue, result.THighPercent!.Value, precision: 1);
        Assert.Equal(kTrue, result.KSlopeCelsius!.Value, precision: 1);
        Assert.True(result.RSquared!.Value > 0.999);
    }

    [Fact]
    public void Detect_SigmoidFit_NoiseyDataIsRobust()
    {
        // Same descending sigmoid with ±2 %T uniform noise. The headline
        // robustness claim of sigmoid fitting over the midpoint method is
        // that Tc stays within ~0.5 °C of truth even on noisy data.
        const double tcTrue = 33.5;
        var rng = new Random(42);
        var points = new List<SpectrumDataPoint>();
        for (var x = 25.0; x <= 45.0001; x += 0.25)
        {
            var u = (x - tcTrue) / 1.5;
            var clean = 5.0 + 90.0 / (1.0 + Math.Exp(u));
            var noise = (rng.NextDouble() - 0.5) * 4.0;
            points.Add(new SpectrumDataPoint { X = x, Y = clean + noise });
        }

        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var result = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SigmoidFit });

        Assert.True(result.HasResult);
        Assert.InRange(result.TemperatureCelsius, tcTrue - 0.5, tcTrue + 0.5);
        Assert.True(result.RSquared!.Value > 0.95);
    }

    [Fact]
    public void Detect_SigmoidFit_PreservesDirectionForCooling()
    {
        // Cooling sweep: file recorded high → low T (firstX > lastX) but the
        // dataset stores points sorted ascending. The fit should still recover
        // Tc and report Direction = Cooling.
        const double tcTrue = 33.5;
        var points = SampleBoltzmann(25, 45, 0.25, 5.0, 95.0, tcTrue, 1.5);
        var dataset = MakeTemperatureScan(points, firstX: 45, lastX: 25);

        var result = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SigmoidFit });

        Assert.True(result.HasResult);
        Assert.Equal(ScanDirection.Cooling, result.Direction);
        Assert.InRange(result.TemperatureCelsius, tcTrue - 0.3, tcTrue + 0.3);
    }

    [Fact]
    public void Detect_SigmoidFit_FlatLineReturnsEmpty()
    {
        // Flat T = 50 % across the whole sweep — there is no transition to
        // fit, so the plateau-difference guard must reject the result.
        var points = new List<SpectrumDataPoint>();
        for (var x = 25.0; x <= 45.0001; x += 0.5)
        {
            points.Add(new SpectrumDataPoint { X = x, Y = 50.0 });
        }

        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var result = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SigmoidFit });

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Detect_SigmoidFit_PopulatesFittedCurveOverEntireGrid()
    {
        // FittedCurve must align 1:1 with the dataset's ascending-X points
        // so the UI can overlay it without re-aligning. Heating-sigmoid
        // shape: the predicted Y starts near T_high (~95) and ends near T_low
        // (~5).
        var points = SampleBoltzmann(25, 45, 0.5, 5.0, 95.0, 33.5, 1.5);
        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);

        var result = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SigmoidFit });

        Assert.True(result.HasResult);
        Assert.NotNull(result.FittedCurve);
        Assert.Equal(points.Count, result.FittedCurve!.Count);
        Assert.True(result.FittedCurve[0] > 90.0);
        Assert.True(result.FittedCurve[^1] < 10.0);
    }

    [Fact]
    public void Detect_SigmoidFit_HandlesUcstAscendingCurve()
    {
        // UCST-like ascending curve (T grows with x). Boltzmann with k < 0
        // describes this orientation. Tc must still be recovered correctly.
        const double tcTrue = 33.5;
        var points = SampleBoltzmann(25, 45, 0.25, 5.0, 95.0, tcTrue, -1.5);
        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);

        var result = CloudPointDetector.Detect(
            dataset,
            new CloudPointDetectionConfig { Method = CloudPointMethod.SigmoidFit });

        Assert.True(result.HasResult);
        Assert.Equal(tcTrue, result.TemperatureCelsius, precision: 1);
        Assert.True(result.KSlopeCelsius!.Value < 0);
    }

    [Fact]
    public void Detect_AmplitudeGate_SuppressesFlatTrace()
    {
        // Flat trace at T=50 % across the sweep — no transition. With the
        // amplitude gate enabled (≥ 5 %T required) the detector returns an
        // empty result before any per-method work runs.
        var points = new List<SpectrumDataPoint>();
        for (var x = 25.0; x <= 45.0001; x += 0.5)
        {
            points.Add(new SpectrumDataPoint { X = x, Y = 50.0 });
        }

        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            MinimumTransitionAmplitudePercent = 5.0,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Detect_AmplitudeGate_AllowsRealTransitionWhenContrastIsSmaller()
    {
        // Same descending sigmoid (amplitude ~90 %T) — amplitude gate of
        // 30 %T is well below the actual span, so detection still runs.
        var points = SampleBoltzmann(25, 45, 0.25, 5.0, 95.0, 33.5, 1.5);
        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            TransmittanceThresholdPercent = 50.0,
            MinimumTransitionAmplitudePercent = 30.0,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.True(result.HasResult);
        Assert.InRange(result.TemperatureCelsius, 32.5, 34.5);
    }

    [Fact]
    public void Detect_PlateauContrastGate_SuppressesNoisyFlatTrace()
    {
        // Random ±2 %T noise around 50 % — no real transition, but the
        // amplitude gate alone might pass on a wide-range noise burst. The
        // plateau-contrast gate checks the edge averages instead, which
        // stays near 50 % in expectation and fails the 10 %T threshold.
        var rng = new Random(7);
        var points = new List<SpectrumDataPoint>();
        for (var x = 25.0; x <= 45.0001; x += 0.5)
        {
            points.Add(new SpectrumDataPoint { X = x, Y = 50.0 + (rng.NextDouble() - 0.5) * 4.0 });
        }

        var dataset = MakeTemperatureScan(points, firstX: 25, lastX: 45);
        var config = new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            MinimumPlateauContrastPercent = 10.0,
        };

        var result = CloudPointDetector.Detect(dataset, config);

        Assert.False(result.HasResult);
    }

    [Fact]
    public void Detect_DefaultConfig_DoesNotApplyAnyNoiseGate()
    {
        // Default config has both gates at 0, so even a 3 %T microtransition
        // produces a result. Pinning this behaviour ensures the new gates
        // are opt-in and cannot regress existing analyses.
        var points = SampleLinear(30, 51.5, 50, 48.5, 21);
        var dataset = MakeTemperatureScan(points, firstX: 30, lastX: 50);

        var result = CloudPointDetector.Detect(dataset, new CloudPointDetectionConfig
        {
            Method = CloudPointMethod.Midpoint,
            TransmittanceThresholdPercent = 50.0,
        });

        Assert.True(result.HasResult);
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

    private static List<SpectrumDataPoint> SampleBoltzmann(
        double start,
        double stop,
        double step,
        double tLow,
        double tHigh,
        double tc,
        double k)
    {
        var result = new List<SpectrumDataPoint>();
        for (var x = start; x <= stop + 1e-9; x += step)
        {
            var y = tLow + (tHigh - tLow) / (1.0 + Math.Exp((x - tc) / k));
            result.Add(new SpectrumDataPoint { X = x, Y = y });
        }
        result.Sort((a, b) => a.X.CompareTo(b.X));
        return result;
    }

    private static List<SpectrumDataPoint> SampleSigmoid(
        double start,
        double stop,
        double step,
        double center,
        double k)
    {
        var result = new List<SpectrumDataPoint>();
        for (var x = start; x <= stop + 1e-9; x += step)
        {
            var y = 100.0 / (1.0 + Math.Exp((x - center) / k));
            result.Add(new SpectrumDataPoint { X = x, Y = y });
        }

        result.Sort((a, b) => a.X.CompareTo(b.X));
        return result;
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
