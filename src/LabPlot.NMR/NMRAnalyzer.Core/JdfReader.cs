using System.Text;

namespace NMRAnalyzer.Core;

/// <summary>
/// Reads JEOL <c>.jdf</c> files, limited to 1D processed (FT- and
/// phase-corrected) spectra.
/// </summary>
/// <remarks>
/// Binary layout reverse-engineered from nmrglue's <c>fileio/jeol.py</c>
/// (github.com/jjhelmus/nmrglue, BSD-3-Clause, Copyright (c) Jonathan J.
/// Helmus). This is an independent re-implementation — no code was copied —
/// covering only what a 1D processed spectrum needs. Four layout traps worth
/// calling out (all handled below):
/// <list type="number">
///   <item>The 1360-byte header is always big-endian; only the data body
///   follows the endian flag at offset 8.</item>
///   <item>Real/imaginary parts are two consecutive blocks (first half real,
///   second half imaginary), NOT interleaved.</item>
///   <item>The stored imaginary section is negated: complex = real - i·section1.</item>
///   <item>The header's <c>data_length</c> field is unreliable; the true
///   length is derived from <c>data_points × sections</c>.</item>
/// </list>
/// The ppm axis is taken from the header's <c>data_axis_start/stop</c> direct
/// values (see <see cref="NmrDataset.XValues"/>), never the sw/obs/car
/// back-calculation, which is off by ~1.8 ppm on processed data.
/// </remarks>
public sealed class JdfReader : INmrDataReader
{
    private const int HeaderSize = 1360;

    // Header field offsets. Every field here is read big-endian regardless of
    // the endian flag (which only governs the body).
    private const int OffTitle = 48;             // ASCII[124]
    private const int OffEndian = 8;             // int8: 0 = big-endian body, 1 = little
    private const int OffInfo = 14;              // byte: dataType = info>>6, dataFormat = info & 0x3F
    private const int OffDataAxisType = 24;      // int8[8]: [0] == 3 means complex
    private const int OffDataPoints = 176;       // uint32[8]
    private const int OffDataOffsetStart = 208;  // uint32[8]
    private const int OffDataOffsetStop = 240;   // uint32[8], inclusive
    private const int OffDataAxisStart = 272;    // float64[8]
    private const int OffDataAxisStop = 336;     // float64[8]
    private const int OffDataStart = 1284;       // uint32: absolute offset of the data body

    private const int OneDimensionalFormat = 1;  // data_format enum value for "one_d"
    private const int ComplexAxisType = 3;       // data_axis_type enum value for "complex"

    public NmrDataset Read(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NMR data file was not found.", filePath);
        }

        using var stream = File.OpenRead(filePath);
        return Parse(stream, filePath);
    }

    /// <summary>
    /// Parse a <c>.jdf</c> stream. Separated from <see cref="Read"/> so tests
    /// can feed a <see cref="MemoryStream"/> without touching the file system
    /// (mirrors <c>JascoSpectrumReader.Parse(TextReader, ...)</c>).
    /// </summary>
    public NmrDataset Parse(Stream stream, string? sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        try
        {
            stream.ReadExactly(header, 0, HeaderSize);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                "File is smaller than a .jdf header; it is truncated or not a JEOL .jdf file.", ex);
        }

        // Trap #1: the header is always big-endian; only the body follows the flag.
        var bodyLittleEndian = (sbyte)header[OffEndian] == 1;

        var info = header[OffInfo];
        var dataType = info >> 6;             // 0 = float64, 1 = float32
        var dataFormat = info & 0x3F;         // 1 = one_d
        if (dataFormat != OneDimensionalFormat)
        {
            throw new NotSupportedException(
                "Only 1D processed .jdf spectra are supported in this version " +
                $"(data_format = {dataFormat}, expected {OneDimensionalFormat}).");
        }

        var isComplex = (sbyte)header[OffDataAxisType] == ComplexAxisType;
        var sections = isComplex ? 2 : 1;

        var pointCount = (int)JdfBinaryHelpers.ReadUInt32(header.AsSpan(OffDataPoints), littleEndian: false);
        if (pointCount <= 0)
        {
            throw new InvalidDataException("The .jdf file declares zero data points.");
        }

        var offsetStart = (int)JdfBinaryHelpers.ReadUInt32(header.AsSpan(OffDataOffsetStart), littleEndian: false);
        var offsetStop = (int)JdfBinaryHelpers.ReadUInt32(header.AsSpan(OffDataOffsetStop), littleEndian: false);
        var axisStart = JdfBinaryHelpers.ReadFloat64(header.AsSpan(OffDataAxisStart), littleEndian: false);
        var axisStop = JdfBinaryHelpers.ReadFloat64(header.AsSpan(OffDataAxisStop), littleEndian: false);
        var dataStart = JdfBinaryHelpers.ReadUInt32(header.AsSpan(OffDataStart), littleEndian: false);
        var title = ReadAsciiString(header.AsSpan(OffTitle, 124));

        // Trap #4: don't trust the header data_length field — derive the body
        // length from data_points × sections × element size.
        var elementSize = dataType == 0 ? 8 : 4;
        var elementCount = pointCount * sections;
        var body = new byte[elementCount * elementSize];
        SeekTo(stream, dataStart);
        try
        {
            stream.ReadExactly(body, 0, body.Length);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                "The .jdf data body is shorter than the header declares; the file is truncated.", ex);
        }

        // Trap #2 + #3: sections are two consecutive blocks (real, then
        // imaginary), not interleaved; the imaginary block is negated.
        var real = new double[pointCount];
        double[]? imaginary = isComplex ? new double[pointCount] : null;
        for (var i = 0; i < pointCount; i++)
        {
            real[i] = ReadElement(body, i, dataType, bodyLittleEndian);
            if (isComplex)
            {
                imaginary![i] = -ReadElement(body, pointCount + i, dataType, bodyLittleEndian);
            }
        }

        // Trim 2ⁿ padding via data_offset_start/stop (stop is inclusive).
        var lo = Math.Clamp(offsetStart, 0, pointCount - 1);
        var hi = Math.Clamp(offsetStop, lo, pointCount - 1);
        var length = hi - lo + 1;

        var trimmedReal = new double[length];
        Array.Copy(real, lo, trimmedReal, 0, length);

        double[]? trimmedImaginary = null;
        if (isComplex)
        {
            trimmedImaginary = new double[length];
            Array.Copy(imaginary!, lo, trimmedImaginary, 0, length);
        }

        return new NmrDataset
        {
            SourceFilePath = sourceFilePath,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Dimensions = 1,
            AxisStartPpm = axisStart,
            AxisStopPpm = axisStop,
            RealValues = trimmedReal,
            ImaginaryValues = trimmedImaginary,
            IsPpmAxis = true,
        };
    }

    private static double ReadElement(byte[] body, int index, int dataType, bool littleEndian) =>
        dataType == 0
            ? JdfBinaryHelpers.ReadFloat64(body.AsSpan(index * 8), littleEndian)
            : JdfBinaryHelpers.ReadFloat32(body.AsSpan(index * 4), littleEndian);

    private static void SeekTo(Stream stream, long absoluteOffset)
    {
        if (stream.CanSeek)
        {
            stream.Seek(absoluteOffset, SeekOrigin.Begin);
            return;
        }

        // Forward-only fallback: skip from the current position (the header
        // has already been consumed, so we can only move forward).
        var toSkip = absoluteOffset - stream.Position;
        if (toSkip < 0)
        {
            throw new InvalidDataException("The .jdf data body precedes the header; the file is malformed.");
        }

        var scratch = new byte[4096];
        while (toSkip > 0)
        {
            var read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, toSkip));
            if (read == 0)
            {
                throw new InvalidDataException("Reached end of stream before the .jdf data body.");
            }

            toSkip -= read;
        }
    }

    private static string ReadAsciiString(ReadOnlySpan<byte> span)
    {
        var end = span.IndexOf((byte)0);
        if (end >= 0)
        {
            span = span[..end];
        }

        return Encoding.ASCII.GetString(span).Trim();
    }
}
