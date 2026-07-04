using System.Buffers.Binary;

namespace NMRAnalyzer.Core;

/// <summary>
/// Low-level endian-aware primitives for decoding JEOL <c>.jdf</c> files.
/// </summary>
/// <remarks>
/// The <c>.jdf</c> header is <b>always</b> big-endian; the data body follows
/// the endian flag stored at header offset 8. Callers pass the body's
/// endianness explicitly so the two-stage layout stays visible at the call
/// site. Note: <see cref="BinaryPrimitives"/> has no
/// <c>ReadDoubleBigEndian</c>, so float64 is read as a <see cref="ulong"/>
/// and bit-cast via <see cref="BitConverter.Int64BitsToDouble"/>.
/// </remarks>
internal static class JdfBinaryHelpers
{
    public static uint ReadUInt32(ReadOnlySpan<byte> src, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(src)
            : BinaryPrimitives.ReadUInt32BigEndian(src);

    public static double ReadFloat64(ReadOnlySpan<byte> src, bool littleEndian)
    {
        var bits = littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(src)
            : BinaryPrimitives.ReadUInt64BigEndian(src);
        return BitConverter.Int64BitsToDouble((long)bits);
    }

    public static float ReadFloat32(ReadOnlySpan<byte> src, bool littleEndian)
    {
        var bits = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(src)
            : BinaryPrimitives.ReadUInt32BigEndian(src);
        return BitConverter.Int32BitsToSingle((int)bits);
    }
}
