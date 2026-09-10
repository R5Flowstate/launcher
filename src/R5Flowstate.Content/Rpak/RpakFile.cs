using System.Buffers;
using System.Buffers.Binary;

namespace R5Flowstate.Content.Rpak;

internal readonly struct UiiaAsset
{
    public required byte[] Header { get; init; }
    public required byte[] Raw { get; init; }
}

/// <summary>Minimal S21 (v8) rpak reader: header tables + page slice for one uiia.</summary>
internal static class RpakFile
{
    public const uint Magic = 0x6B615052; // 'RPak'
    public const int HeaderSizeV8 = 0x80;
    public const uint TypeUiia = 0x61696975; // 'uiia'
    public const ushort FlagRtech = 1 << 8;
    public const ushort FlagOodle = 1 << 9;
    public const ushort FlagZstd = 1 << 15;

    public static bool TryExtractUiia(string path, out UiiaAsset asset, out string? error)
    {
        asset = default;
        error = null;

        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (file.Length < HeaderSizeV8)
        {
            error = "too small";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0)) != Magic)
        {
            error = "not rpak";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        if (version != 8)
        {
            error = "rpak v" + version;
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        if ((flags & FlagRtech) != 0)
        {
            error = "rtech encoded";
            return false;
        }
        if ((flags & FlagZstd) != 0)
        {
            error = "zstd encoded";
            return false;
        }

        if ((flags & FlagOodle) == 0)
            return TryParseUiia(file, out asset, out error);

        var dsize = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(0x30));
        if (dsize < HeaderSizeV8 || dsize > int.MaxValue)
        {
            error = "bad decompressed size";
            return false;
        }

        OodleNative.HintSearchPath(Path.GetDirectoryName(path));
        OodleNative.HintSearchPath(Path.GetFullPath(Path.Combine(path, "..", "..")));

        var rawLen = (int)dsize - HeaderSizeV8;
        var rented = ArrayPool<byte>.Shared.Rent((int)dsize);
        try
        {
            Buffer.BlockCopy(file, 0, rented, 0, HeaderSizeV8);
            if (!OodleNative.TryDecompress(
                    file, HeaderSizeV8, file.Length - HeaderSizeV8,
                    rented, HeaderSizeV8, rawLen))
            {
                error = OodleNative.IsAvailable ? "oodle decode failed" : "oodle dll missing";
                return false;
            }

            return TryParseUiia(rented, out asset, out error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryParseUiia(byte[] buf, out UiiaAsset asset, out string? error)
    {
        asset = default;
        error = null;

        var star = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x48));
        var opt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x4A));
        var nseg = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x4C));
        var npage = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x4E));
        var nptr = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x54));
        var nasset = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x58));
        var nguid = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x5C));
        var ndep = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x60));
        var nextref = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x64));
        var extsz = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x68));
        var unk74 = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x74));
        var unk78 = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x78));

        if (npage <= 0 || nasset <= 0 || nseg < 0 || nptr < 0)
        {
            error = "empty pak";
            return false;
        }

        var off = HeaderSizeV8 + star + opt;
        if (off + nseg * 16 + npage * 12 > buf.Length)
        {
            error = "truncated tables";
            return false;
        }

        off += nseg * 16;

        var pageSizes = new int[npage];
        for (var i = 0; i < npage; i++)
        {
            pageSizes[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 8));
            off += 12;
        }

        off += nptr * 8;
        if (off < 0 || off + nasset * 80 > buf.Length)
        {
            error = "truncated assets";
            return false;
        }

        var assetsOff = off;
        off += nasset * 80;
        off += nguid * 8 + ndep * 4 + nextref * 4 + extsz + unk74 + unk78;
        if (off < 0 || off > buf.Length)
        {
            error = "truncated pages";
            return false;
        }

        var pageOff = new int[npage];
        var cur = off;
        for (var i = 0; i < npage; i++)
        {
            if (pageSizes[i] < 0 || cur + pageSizes[i] > buf.Length)
            {
                error = "page overrun";
                return false;
            }
            pageOff[i] = cur;
            cur += pageSizes[i];
        }

        for (var i = 0; i < nasset; i++)
        {
            var a = assetsOff + i * 80;
            var type = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(a + 76));
            if (type != TypeUiia)
                continue;

            var headIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(a + 16));
            var headOff = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(a + 20));
            var cpuIdx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(a + 24));
            var cpuOff = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(a + 28));

            if (headIdx < 0 || headIdx >= npage || cpuIdx < 0 || cpuIdx >= npage)
            {
                error = "bad page ptr";
                return false;
            }

            if (headOff < 0 || headOff + 64 > pageSizes[headIdx])
            {
                error = "bad uiia header";
                return false;
            }

            if (cpuOff < 0 || cpuOff >= pageSizes[cpuIdx])
            {
                error = "bad uiia data";
                return false;
            }

            var header = new byte[64];
            Buffer.BlockCopy(buf, pageOff[headIdx] + headOff, header, 0, 64);

            var rawLen = pageSizes[cpuIdx] - cpuOff;
            var raw = new byte[rawLen];
            Buffer.BlockCopy(buf, pageOff[cpuIdx] + cpuOff, raw, 0, rawLen);

            asset = new UiiaAsset { Header = header, Raw = raw };
            return true;
        }

        error = "no uiia";
        return false;
    }

    public static string DescribeError(string path, string? error) =>
        $"{Path.GetFileName(path)}: {error ?? "failed"}";
}
