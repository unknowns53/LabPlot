using System;
using System.Buffers.Binary;
using System.IO;
using ScottPlot;

namespace LabPlot.Core.Wpf.Helpers;

/// <summary>
/// Output format selected by the user from the グラフ保存 dialog. The
/// PNG / SVG split is uniform across GPC / Spectrum / DLS so the enum
/// lives here rather than being redeclared per-app.
/// </summary>
public enum GraphSaveFormat
{
    Png,
    Svg,
}

/// <summary>
/// Cross-app graph save / export helpers. Centralises the PNG + SVG
/// rendering policy (3600x2160 / 300 dpi by default), plus the small
/// PNG <c>pHYs</c> chunk patch that bakes the DPI metadata into the
/// file. GPC / Spectrum / DLS used to keep three near-identical copies
/// of this code; each app now calls the shared helpers below.
/// </summary>
public static class GraphSaveHelpers
{
    public const int ExportDpi = 300;
    public const float DisplayDpi = 96f;
    public const int DefaultExportWidth = 3600;
    public const int DefaultExportHeight = 2160;
    public const int SquareExportWidth = 3000;

    /// <summary>
    /// Resolve the output format from a save-file-dialog return. Honors
    /// the user-typed extension first (both <c>.png</c> and <c>.svg</c>
    /// pin the format regardless of the active filter), then falls back
    /// to the dialog's 1-based <paramref name="filterIndex"/>
    /// (1 = PNG, 2 = SVG) which matches the standard
    /// "PNG画像 (*.png)|*.png|SVGベクター画像 (*.svg)|*.svg" filter
    /// shipped by all three apps.
    /// </summary>
    public static GraphSaveFormat GetGraphSaveFormat(string filePath, int filterIndex)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return GraphSaveFormat.Svg;
        }
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return GraphSaveFormat.Png;
        }

        return filterIndex == 2
            ? GraphSaveFormat.Svg
            : GraphSaveFormat.Png;
    }

    /// <summary>
    /// Force the file extension to match the resolved format so a
    /// user-typed name like "graph" lands on disk as "graph.png" or
    /// "graph.svg" rather than as the verbatim string.
    /// </summary>
    public static string EnsureGraphSaveFileExtension(string filePath, GraphSaveFormat saveFormat)
    {
        var expected = saveFormat == GraphSaveFormat.Svg ? ".svg" : ".png";
        if (string.Equals(Path.GetExtension(filePath), expected, StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }
        return Path.ChangeExtension(filePath, expected);
    }

    /// <summary>
    /// Pick an output resolution from the GraphFormatPanel aspect ratio.
    /// <para>
    /// <paramref name="aspectRatio"/> is the panel's "Auto" / "16:9" /
    /// etc. selection translated to width / height; pass <c>null</c>
    /// when the user selected Auto so we fall back to the
    /// <see cref="DefaultExportWidth"/> x <see cref="DefaultExportHeight"/>
    /// landscape default.
    /// </para>
    /// </summary>
    public static (int Width, int Height) GetExportImageSize(double? aspectRatio)
    {
        if (!aspectRatio.HasValue)
        {
            return (DefaultExportWidth, DefaultExportHeight);
        }

        var width = aspectRatio.Value == 1
            ? SquareExportWidth
            : DefaultExportWidth;
        var height = Math.Max(1, (int)Math.Round(width / aspectRatio.Value));
        return (width, height);
    }

    /// <summary>
    /// Render the plot as inline SVG and write it verbatim. ScottPlot's
    /// SVG output is HTML-wrapped (so it can be embedded in a page);
    /// downstream tooling that needs a bare &lt;svg&gt; document will
    /// have to slice the prologue itself.
    /// </summary>
    public static void SaveGraphSvg(Plot plot, string filePath, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(plot);
        var svg = plot.GetSvgHtml(width, height);
        File.WriteAllText(filePath, svg);
    }

    /// <summary>
    /// Save a PNG and bake a <c>pHYs</c> chunk so Word / Illustrator
    /// pick up the correct DPI rather than the default 72 dpi guess.
    /// Use <see cref="ExportDpi"/> (300) unless you have a specific
    /// reason to deviate.
    /// </summary>
    public static void SaveGraphPng(Plot plot, string filePath, int width, int height, int dpi)
    {
        ArgumentNullException.ThrowIfNull(plot);
        plot.SavePng(filePath, width, height);
        ApplyPngDpiMetadata(filePath, dpi);
    }

    private static void ApplyPngDpiMetadata(string filePath, int dpi)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (!HasPngSignature(bytes))
        {
            return;
        }

        var pixelsPerMeter = checked((uint)Math.Round(dpi / 0.0254));
        var physicalPixelDimensionsChunk = CreatePngPhysicalPixelDimensionsChunk(pixelsPerMeter);
        var offset = 8;
        var insertOffset = -1;

        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue || offset + 12 + (int)length > bytes.Length)
            {
                return;
            }

            var chunkLength = 12 + (int)length;
            var chunkTypeOffset = offset + 4;
            if (PngChunkTypeEquals(bytes, chunkTypeOffset, "pHYs"))
            {
                File.WriteAllBytes(filePath, ReplaceBytes(bytes, offset, chunkLength, physicalPixelDimensionsChunk));
                return;
            }

            if (PngChunkTypeEquals(bytes, chunkTypeOffset, "IHDR"))
            {
                insertOffset = offset + chunkLength;
            }

            offset += chunkLength;
        }

        if (insertOffset > 0)
        {
            File.WriteAllBytes(filePath, InsertBytes(bytes, insertOffset, physicalPixelDimensionsChunk));
        }
    }

    private static byte[] CreatePngPhysicalPixelDimensionsChunk(uint pixelsPerMeter)
    {
        const int chunkDataLength = 9;
        var chunk = new byte[4 + 4 + chunkDataLength + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), chunkDataLength);
        chunk[4] = (byte)'p';
        chunk[5] = (byte)'H';
        chunk[6] = (byte)'Y';
        chunk[7] = (byte)'s';
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8, 4), pixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12, 4), pixelsPerMeter);
        chunk[16] = 1;

        var crc = CalculatePngCrc(chunk.AsSpan(4, 4 + chunkDataLength));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17, 4), crc);
        return chunk;
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static bool HasPngSignature(byte[] bytes)
    {
        return bytes.Length >= 8
            && bytes[0] == 137
            && bytes[1] == 80
            && bytes[2] == 78
            && bytes[3] == 71
            && bytes[4] == 13
            && bytes[5] == 10
            && bytes[6] == 26
            && bytes[7] == 10;
    }

    private static bool PngChunkTypeEquals(byte[] bytes, int offset, string type)
    {
        return offset + 4 <= bytes.Length
            && bytes[offset] == (byte)type[0]
            && bytes[offset + 1] == (byte)type[1]
            && bytes[offset + 2] == (byte)type[2]
            && bytes[offset + 3] == (byte)type[3];
    }

    private static byte[] InsertBytes(byte[] source, int offset, byte[] insertion)
    {
        var result = new byte[source.Length + insertion.Length];
        Buffer.BlockCopy(source, 0, result, 0, offset);
        Buffer.BlockCopy(insertion, 0, result, offset, insertion.Length);
        Buffer.BlockCopy(source, offset, result, offset + insertion.Length, source.Length - offset);
        return result;
    }

    private static byte[] ReplaceBytes(byte[] source, int offset, int count, byte[] replacement)
    {
        var result = new byte[source.Length - count + replacement.Length];
        Buffer.BlockCopy(source, 0, result, 0, offset);
        Buffer.BlockCopy(replacement, 0, result, offset, replacement.Length);
        var sourceTailOffset = offset + count;
        Buffer.BlockCopy(source, sourceTailOffset, result, offset + replacement.Length, source.Length - sourceTailOffset);
        return result;
    }
}
