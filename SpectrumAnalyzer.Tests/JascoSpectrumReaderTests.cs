using System.Text;
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
        Assert.True(dataset.IsTemperatureScan);
        Assert.False(dataset.IsWavelengthScan);
        Assert.Equal(50.0, dataset.RawFirstX);
        Assert.Equal(90.0, dataset.RawLastX);
        Assert.Equal(ScanDirection.Heating, dataset.OriginalScanDirection);
    }

    [Fact]
    public void Parse_PreservesFirstxLastxForCoolingScan()
    {
        const string sample = """
            TITLE
            XUNITS	Temperature[C]
            YUNITS	TRANSMITTANCE
            FIRSTX	90
            LASTX	50
            NPOINTS	3
            XYDATA
            89.98	13.5549
            70.03	62.5
            50.01	101.0

            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), "cooling.txt");

        Assert.Equal(90.0, dataset.RawFirstX);
        Assert.Equal(50.0, dataset.RawLastX);
        Assert.Equal(ScanDirection.Cooling, dataset.OriginalScanDirection);
    }

    [Fact]
    public void Parse_FlagsWavelengthScan()
    {
        const string sample = """
            XUNITS	NANOMETERS
            YUNITS	ABSORBANCE
            FIRSTX	800
            LASTX	200
            XYDATA
            200	0.10
            500	0.85
            800	0.05

            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), null);

        Assert.True(dataset.IsWavelengthScan);
        Assert.False(dataset.IsTemperatureScan);
        Assert.Equal(800.0, dataset.RawFirstX);
        Assert.Equal(200.0, dataset.RawLastX);
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
    public void Parse_ExtractsTemperatureFooterMetadata()
    {
        const string sample = """
            TITLE
            XUNITS	Temperature[C]
            YUNITS	TRANSMITTANCE
            FIRSTX	50
            LASTX	90
            XYDATA
            50.01	100.124
            70.03	91.0084
            90.05	14.8993

            [コメント情報]
            タイトル
            オペレーター

            [測定情報]
            機種名	V-750
            測光モード	%T
            UV/Vis バンド幅	2 nm
            測定波長	500 nm

            [付属品情報]
            付属品	ETC-505
            温度勾配	1 C/min
            試料センサー	ホルダー
            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), "footer.txt");

        Assert.Equal("500 nm", dataset.MeasurementWavelengthText);
        Assert.Equal("1 C/min", dataset.TemperatureRampRateText);
        Assert.Equal("ETC-505", dataset.AccessoryName);
        Assert.Equal("2 nm", dataset.BandPassText);
        Assert.Equal("%T", dataset.PhotometricMode);

        // Empty footer slots like the comment-section "タイトル	" line must
        // not shadow real header keys, so they should be dropped instead of
        // being stored with an empty value.
        Assert.False(dataset.Metadata.ContainsKey("タイトル"));
        Assert.False(dataset.Metadata.ContainsKey("オペレーター"));
        Assert.True(dataset.Metadata.ContainsKey("機種名"));
        Assert.Equal("V-750", dataset.Metadata["機種名"]);
    }

    [Fact]
    public void Parse_LeavesMetadataEmptyWhenNoFooter()
    {
        const string sample = """
            XUNITS	NANOMETERS
            YUNITS	ABSORBANCE
            XYDATA
            200	0.10
            500	0.85
            """;

        var dataset = new JascoSpectrumReader().Parse(new StringReader(sample), null);

        Assert.Empty(dataset.Metadata);
        Assert.Null(dataset.MeasurementWavelengthText);
        Assert.Null(dataset.TemperatureRampRateText);
        Assert.Null(dataset.AccessoryName);
    }

    [Fact]
    public void Read_DecodesShiftJisFooterFromActualFile()
    {
        // Round-trip a Shift-JIS encoded file through the file-based Read
        // method to confirm the encoding handling, not just the line-level
        // parsing logic.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding("shift_jis");

        const string sample =
            "TITLE\t\n" +
            "XUNITS\tTemperature[C]\n" +
            "YUNITS\tTRANSMITTANCE\n" +
            "FIRSTX\t50\n" +
            "LASTX\t90\n" +
            "XYDATA\n" +
            "50.01\t100.124\n" +
            "90.05\t14.8993\n" +
            "\n" +
            "[付属品情報]\n" +
            "付属品\tETC-505\n" +
            "温度勾配\t1 C/min\n";

        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, sample, sjis);
            var dataset = new JascoSpectrumReader().Read(temp);

            Assert.Equal("ETC-505", dataset.AccessoryName);
            Assert.Equal("1 C/min", dataset.TemperatureRampRateText);
        }
        finally
        {
            File.Delete(temp);
        }
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
