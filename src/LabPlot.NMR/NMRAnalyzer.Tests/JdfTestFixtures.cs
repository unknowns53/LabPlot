using System.Buffers.Binary;

namespace NMRAnalyzer.Tests;

/// <summary>
/// Builds synthetic JEOL <c>.jdf</c> byte streams for reader tests. Binary
/// formats can't be embedded as raw string literals the way JASCO text
/// exports can, so instead of checking a fixture file into the repo we
/// assemble the header + body here where every field's meaning is spelled
/// out in code. Only the fields <see cref="NMRAnalyzer.Core.JdfReader"/>
/// actually reads are populated; the rest of the 1360-byte header stays zero.
/// </summary>
internal static class JdfTestFixtures
{
    private const int HeaderSize = 1360;

    // Offsets mirror JdfReader's private constants.
    private const int OffEndian = 8;
    private const int OffInfo = 14;
    private const int OffDataAxisType = 24;
    private const int OffDataPoints = 176;
    private const int OffDataOffsetStart = 208;
    private const int OffDataOffsetStop = 240;
    private const int OffDataAxisStart = 272;
    private const int OffDataAxisStop = 336;
    private const int OffDataStart = 1284;

    /// <summary>
    /// Build a minimal float64 1D complex spectrum. <paramref name="imaginary"/>
    /// null produces a real-only spectrum. <paramref name="trimStop"/> is the
    /// inclusive last index kept after truncation (defaults to the last point).
    /// </summary>
    public static byte[] BuildMinimal1D(
        double[] real,
        double[]? imaginary,
        double axisStartPpm,
        double axisStopPpm,
        bool bodyLittleEndian = false,
        int trimStart = 0,
        int? trimStop = null,
        int dataFormat = 1)
    {
        var isComplex = imaginary is not null;
        var header = new byte[HeaderSize];

        // Endian flag: 0 = big-endian body, 1 = little. The header itself is
        // always written big-endian below.
        header[OffEndian] = (byte)(bodyLittleEndian ? 1 : 0);

        // info byte: dataType (bits 7-6, 0 = float64) | dataFormat (bits 5-0).
        header[OffInfo] = (byte)(dataFormat & 0x3F);

        // data_axis_type[0]: 3 = complex, 1 = real.
        header[OffDataAxisType] = (byte)(isComplex ? 3 : 1);

        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(OffDataPoints), (uint)real.Length);

        var stop = trimStop ?? real.Length - 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(OffDataOffsetStart), (uint)trimStart);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(OffDataOffsetStop), (uint)stop);

        BinaryPrimitives.WriteUInt64BigEndian(
            header.AsSpan(OffDataAxisStart), (ulong)BitConverter.DoubleToInt64Bits(axisStartPpm));
        BinaryPrimitives.WriteUInt64BigEndian(
            header.AsSpan(OffDataAxisStop), (ulong)BitConverter.DoubleToInt64Bits(axisStopPpm));

        // Body immediately follows the header.
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(OffDataStart), HeaderSize);

        var sections = isComplex ? 2 : 1;
        var body = new byte[real.Length * sections * 8];
        WriteSection(body.AsSpan(0, real.Length * 8), real, bodyLittleEndian);
        if (isComplex)
        {
            // The reader negates section1 to recover the true imaginary part,
            // so store the negation here for a correct round-trip.
            var stored = new double[imaginary!.Length];
            for (var i = 0; i < stored.Length; i++)
            {
                stored[i] = -imaginary[i];
            }

            WriteSection(body.AsSpan(real.Length * 8), stored, bodyLittleEndian);
        }

        var file = new byte[header.Length + body.Length];
        header.CopyTo(file, 0);
        body.CopyTo(file, header.Length);
        return file;
    }

    private static void WriteSection(Span<byte> dest, double[] values, bool littleEndian)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var bits = (ulong)BitConverter.DoubleToInt64Bits(values[i]);
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(i * 8), bits);
            }
            else
            {
                BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(i * 8), bits);
            }
        }
    }
}
