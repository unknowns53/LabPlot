using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class SpectrumYAxisConverterTests
{
    [Theory]
    [InlineData(0.0, 100.0)]
    [InlineData(1.0, 10.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(3.0, 0.1)]
    public void AbsorbanceToTransmittance_FollowsBeerLambert(double absorbance, double expectedPercent)
    {
        var actual = SpectrumYAxisConverter.AbsorbanceToTransmittancePercent(absorbance);
        Assert.Equal(expectedPercent, actual, precision: 9);
    }

    [Theory]
    [InlineData(100.0, 0.0)]
    [InlineData(10.0, 1.0)]
    [InlineData(1.0, 2.0)]
    public void TransmittanceToAbsorbance_FollowsBeerLambert(double transmittancePercent, double expectedAbsorbance)
    {
        var actual = SpectrumYAxisConverter.TransmittancePercentToAbsorbance(transmittancePercent);
        Assert.Equal(expectedAbsorbance, actual, precision: 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void TransmittanceToAbsorbance_ReturnsNaNForInvalidInput(double transmittancePercent)
    {
        var actual = SpectrumYAxisConverter.TransmittancePercentToAbsorbance(transmittancePercent);
        Assert.True(double.IsNaN(actual));
    }

    [Fact]
    public void RoundTrip_AbsorbanceToTransmittanceAndBack_PreservesValue()
    {
        const double original = 0.7;
        var transmittance = SpectrumYAxisConverter.AbsorbanceToTransmittancePercent(original);
        var roundTripped = SpectrumYAxisConverter.TransmittancePercentToAbsorbance(transmittance);
        Assert.Equal(original, roundTripped, precision: 9);
    }

    [Fact]
    public void GetDisplayYValues_AbsorbanceDataset_ReturnsConvertedTransmittance()
    {
        var dataset = MakeDataset("ABSORBANCE", new[] { 0.0, 1.0, 2.0 });

        var values = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Transmittance);

        Assert.Equal(new[] { 100.0, 10.0, 1.0 }, values, new DoubleComparer(1e-9));
    }

    [Fact]
    public void GetDisplayYValues_NativeMode_ReturnsDatasetReference()
    {
        var dataset = MakeDataset("ABSORBANCE", new[] { 0.5, 1.5 });

        var values = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Native);

        Assert.Same(dataset.YValues, values);
    }

    [Fact]
    public void GetDisplayYValues_ReflectanceDataset_FallsBackToNativeWhenAOrTRequested()
    {
        var dataset = MakeDataset("REFLECTANCE", new[] { 12.5, 25.0 });

        var values = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);

        Assert.Same(dataset.YValues, values);
    }

    [Theory]
    [InlineData("ABSORBANCE", YAxisDisplayMode.Native, true)]
    [InlineData("ABSORBANCE", YAxisDisplayMode.Absorbance, true)]
    [InlineData("ABSORBANCE", YAxisDisplayMode.Transmittance, true)]
    [InlineData("TRANSMITTANCE", YAxisDisplayMode.Absorbance, true)]
    [InlineData("TRANSMITTANCE", YAxisDisplayMode.Transmittance, true)]
    [InlineData("REFLECTANCE", YAxisDisplayMode.Absorbance, false)]
    [InlineData("REFLECTANCE", YAxisDisplayMode.Transmittance, false)]
    [InlineData("REFLECTANCE", YAxisDisplayMode.Native, true)]
    public void CanDisplay_ReflectsConversionFeasibility(string yUnits, YAxisDisplayMode mode, bool expected)
    {
        var dataset = MakeDataset(yUnits, new[] { 1.0 });
        Assert.Equal(expected, SpectrumYAxisConverter.CanDisplay(dataset, mode));
    }

    [Fact]
    public void GetDisplayYLabel_TransmittanceMode_ReturnsPercentLabel()
    {
        var dataset = MakeDataset("ABSORBANCE", new[] { 1.0 });

        Assert.Equal("Transmittance / %", SpectrumYAxisConverter.GetDisplayYLabel(dataset, YAxisDisplayMode.Transmittance));
    }

    [Fact]
    public void GetDisplayYLabel_NativeMode_ReturnsDatasetLabel()
    {
        var dataset = MakeDataset("ABSORBANCE", new[] { 1.0 });

        Assert.Equal("Absorbance", SpectrumYAxisConverter.GetDisplayYLabel(dataset, YAxisDisplayMode.Native));
    }

    [Fact]
    public void GetDisplayPoints_TransmittanceDataset_ConvertsYButPreservesX()
    {
        var dataset = MakeDataset("TRANSMITTANCE", new[] { 100.0, 10.0, 1.0 });

        var points = SpectrumYAxisConverter.GetDisplayPoints(dataset, YAxisDisplayMode.Absorbance);

        Assert.Equal(3, points.Count);
        Assert.Equal(0.0, points[0].Y, precision: 9);
        Assert.Equal(1.0, points[1].Y, precision: 9);
        Assert.Equal(2.0, points[2].Y, precision: 9);
        // X is sequential 1, 2, 3 from MakeDataset.
        Assert.Equal(1.0, points[0].X);
        Assert.Equal(3.0, points[2].X);
    }

    private static SpectrumDataset MakeDataset(string rawYUnits, double[] yValues)
    {
        var points = new SpectrumDataPoint[yValues.Length];
        for (var i = 0; i < yValues.Length; i++)
        {
            points[i] = new SpectrumDataPoint { X = i + 1, Y = yValues[i] };
        }

        return new SpectrumDataset
        {
            RawXUnits = "NANOMETERS",
            RawYUnits = rawYUnits,
            XLabel = "Wavelength / nm",
            YLabel = rawYUnits == "ABSORBANCE" ? "Absorbance" : rawYUnits == "TRANSMITTANCE" ? "Transmittance / %" : rawYUnits,
            Points = points,
        };
    }

    private sealed class DoubleComparer : IEqualityComparer<double>
    {
        private readonly double _tolerance;

        public DoubleComparer(double tolerance) => _tolerance = tolerance;

        public bool Equals(double x, double y) => Math.Abs(x - y) <= _tolerance;

        public int GetHashCode(double obj) => obj.GetHashCode();
    }
}
