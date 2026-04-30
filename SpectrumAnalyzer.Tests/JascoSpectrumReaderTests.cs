using SpectrumAnalyzer.Core;

namespace SpectrumAnalyzer.Tests;

public sealed class JascoSpectrumReaderTests
{
    [Fact]
    public void Parse_ReadsWavelengthScanWithDescendingX()
    {
        const string sample = """
            TITLE
            DATA TYPE
            ORIGIN	JASCO
            DATE	23/06/01
            XUNITS	NANOMETERS
            YUNITS	ABSORBANCE
            FIRSTX	800
            LASTX	200
            NPOINTS	4
            DELTAX	-1
            XYDATA
            800	0.85
            799	0.86
            201	-0.94
            200	-1.19

            [�R�����g���]
            ������	thf
            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), "wavelength.txt");

        Assert.Equal("Wavelength / nm", dataset.XLabel);
        Assert.Equal("Absorbance", dataset.YLabel);
        Assert.Equal("NANOMETERS", dataset.RawXUnits);
        Assert.Equal("ABSORBANCE", dataset.RawYUnits);
        Assert.Equal(4, dataset.Points.Count);
        Assert.Equal(200, dataset.Points[0].X);
        Assert.Equal(-1.19, dataset.Points[0].Y);
        Assert.Equal(800, dataset.Points[3].X);
        Assert.Equal(0.85, dataset.Points[3].Y);
    }

    [Fact]
    public void Parse_ReadsTemperatureScan()
    {
        const string sample = """
            TITLE
            ORIGIN	JASCO
            XUNITS	Temperature[C]
            YUNITS	TRANSMITTANCE
            FIRSTX	50
            LASTX	90
            NPOINTS	3
            XYDATA
            50.01	100.124
            70.03	91.0084
            90.05	14.8993

            [�f�[�^���]
            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), "temperature.txt");

        Assert.Equal("Temperature / °C", dataset.XLabel);
        Assert.Equal("Transmittance / %", dataset.YLabel);
        Assert.Equal(3, dataset.Points.Count);
        Assert.Equal(50.01, dataset.Points[0].X);
        Assert.Equal(90.05, dataset.Points[2].X);
    }

    [Fact]
    public void Parse_ThrowsWhenMissingXyDataMarker()
    {
        const string sample = """
            TITLE
            ORIGIN	JASCO
            XUNITS	NANOMETERS
            """;

        Assert.Throws<InvalidDataException>(() => new JascoSpectrumReader().Parse(new StringReader(sample), null));
    }

    [Fact]
    public void Parse_ThrowsWhenNoNumericRows()
    {
        const string sample = """
            TITLE
            XUNITS	NANOMETERS
            YUNITS	ABSORBANCE
            XYDATA
            """;

        Assert.Throws<InvalidDataException>(() => new JascoSpectrumReader().Parse(new StringReader(sample), null));
    }

    [Fact]
    public void Parse_ReadsInfraredCommaSeparatedExport()
    {
        const string sample = """
            TITLE,
            DATA TYPE,INFRARED SPECTRUM
            ORIGIN,JASCO
            DATE,25/08/18
            XUNITS,1/CM
            YUNITS,TRANSMITTANCE
            FIRSTX,  499.4729
            LASTX, 4200.2010
            NPOINTS,    4
            DELTAX,0.964233
            XYDATA
            499.4729,64.2295
            500.4372,73.5521
            501.4014,87.6568
            502.3656,97.5824

            [�R�����g���]
            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), "ir.csv");

        Assert.Equal("Wavenumber / cm⁻¹", dataset.XLabel);
        Assert.Equal("Transmittance / %", dataset.YLabel);
        Assert.Equal("1/CM", dataset.RawXUnits);
        Assert.Equal("TRANSMITTANCE", dataset.RawYUnits);
        Assert.Equal("INFRARED SPECTRUM", dataset.RawDataType);
        Assert.Equal(4, dataset.Points.Count);
        Assert.Equal(499.4729, dataset.Points[0].X);
        Assert.Equal(64.2295, dataset.Points[0].Y);
        Assert.Equal(502.3656, dataset.Points[3].X);
        Assert.Equal(97.5824, dataset.Points[3].Y);
    }

    [Fact]
    public void Parse_PreservesIrSentinelValuesVerbatim()
    {
        // JASCO FTIR exports occasionally emit -1.17549E-038 (≈ -FLT_MIN) for
        // points that were not measured. The parser keeps those points so that
        // downstream callers can decide how to handle them.
        const string sample = """
            DATA TYPE,INFRARED SPECTRUM
            XUNITS,1/CM
            YUNITS,TRANSMITTANCE
            XYDATA
            499.4729,520.799
            500.4372,-1.17549E-038
            501.4014,64.3598
            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), null);

        Assert.Equal(3, dataset.Points.Count);
        Assert.Equal(520.799, dataset.Points[0].Y);
        Assert.Equal(-1.17549E-038, dataset.Points[1].Y);
        Assert.Equal(64.3598, dataset.Points[2].Y);
    }

    [Fact]
    public void Parse_PreservesUnknownUnitsVerbatim()
    {
        const string sample = """
            TITLE
            XUNITS	CustomXUnit
            YUNITS	CustomYUnit
            XYDATA
            1	2

            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), null);

        Assert.Equal("CustomXUnit", dataset.XLabel);
        Assert.Equal("CustomYUnit", dataset.YLabel);
    }
}
