using NMRAnalyzer.Core;

namespace NMRAnalyzer.Tests;

public class NmrIntegratorTests
{
    private static NmrDataset Dataset(double[] ppmDescending, double[] intensity) => new()
    {
        AxisStartPpm = ppmDescending[0],
        AxisStopPpm = ppmDescending[^1],
        RealValues = intensity,
    };

    [Fact]
    public void Integrate_ConstantSignalGivesRectangleArea()
    {
        // Flat intensity 2.0 over ppm 0..10 (stored descending). Integrating
        // 1..9 with no baseline gives 2 * 8 = 16.
        var ppm = new double[11];
        var y = new double[11];
        for (var i = 0; i < 11; i++)
        {
            ppm[i] = 10.0 - i; // 10 down to 0
            y[i] = 2.0;
        }

        var region = new NmrIntegrationRegion
        {
            Label = "A",
            PpmMin = 1.0,
            PpmMax = 9.0,
            Baseline = NmrBaselineMode.None,
        };

        var result = NmrIntegrator.Integrate(Dataset(ppm, y), region);

        Assert.Equal(16.0, result.Area, precision: 6);
    }

    [Fact]
    public void Integrate_LinearBaselineSubtractsChord()
    {
        // A triangular signal sitting on a sloped baseline: linear baseline
        // subtraction should remove the chord contribution, leaving the
        // area of the triangle above the chord.
        // ppm 0..4 descending, y = baseline (x) + a symmetric tent of height 2.
        var ppm = new[] { 4.0, 3.0, 2.0, 1.0, 0.0 };
        var y = new[]
        {
            4.0 + 0.0, // x=4
            3.0 + 1.0, // x=3
            2.0 + 2.0, // x=2 (tent apex)
            1.0 + 1.0, // x=1
            0.0 + 0.0, // x=0
        };

        var region = new NmrIntegrationRegion
        {
            Label = "tent",
            PpmMin = 0.0,
            PpmMax = 4.0,
            Baseline = NmrBaselineMode.Linear,
        };

        var result = NmrIntegrator.Integrate(Dataset(ppm, y), region);

        // Endpoints y(0)=0 and y(4)=4 define the chord; the tent above it has
        // base 4 and height 2 => triangle area = 0.5 * 4 * 2 = 4.
        Assert.Equal(4.0, result.Area, precision: 6);
    }

    [Fact]
    public void NormalizeToReference_ScalesRatiosToReferenceRegion()
    {
        var ppm = new double[11];
        var y = new double[11];
        for (var i = 0; i < 11; i++)
        {
            ppm[i] = 10.0 - i;
            y[i] = 1.0;
        }

        var dataset = Dataset(ppm, y);
        var one = NmrIntegrator.Integrate(dataset, new NmrIntegrationRegion
        {
            Label = "ref", PpmMin = 0.0, PpmMax = 2.0, Baseline = NmrBaselineMode.None,
        });
        var three = NmrIntegrator.Integrate(dataset, new NmrIntegrationRegion
        {
            Label = "wide", PpmMin = 0.0, PpmMax = 6.0, Baseline = NmrBaselineMode.None,
        });

        // Reference area = 2, target area = 6 => ratio 3 when reference = 1.
        var normalized = NmrIntegrator.NormalizeToReference(new[] { one, three }, referenceIndex: 0);

        Assert.Equal(1.0, normalized[0].Ratio, precision: 6);
        Assert.Equal(3.0, normalized[1].Ratio, precision: 6);
    }

    [Fact]
    public void Integrate_RegionOutsideRangeReturnsEmpty()
    {
        var ppm = new[] { 5.0, 4.0, 3.0, 2.0, 1.0 };
        var y = new[] { 1.0, 1.0, 1.0, 1.0, 1.0 };

        var result = NmrIntegrator.Integrate(Dataset(ppm, y), new NmrIntegrationRegion
        {
            Label = "oob", PpmMin = 6.0, PpmMax = 8.0,
        });

        Assert.False(result.HasResult);
    }
}
