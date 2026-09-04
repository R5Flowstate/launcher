using System.Buffers;
using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace R5Flowstate.Content.Rpak;

/// <summary>
/// Decode a uiia v2 asset to 32-bit BGRA. Models the engine tile transform:
/// 32px tiles, 31px stride, Morton block order, imgFlags&amp;3 routing.
/// </summary>
internal static class UiiaDecoder
{
    public const int TilePx = 32;
    public const int TileStride = 31;
    public const int BlockPx = 4;
    public const int BlocksPerTile = TilePx / BlockPx; // 8

    public const byte OpcodeBc1 = 0x40;
    public const byte OpcodeBc7 = 0x41;
    public const byte OpcodeCopy = 0xC0;

    private static readonly (int X, int Y)[] s_demorton = BuildDemorton();

    public static bool TryDecode(
        in UiiaAsset asset,
        int maxWidth,
        out int width,
        out int height,
        out int sourceWidth,
        out byte[] bgra,
        out string? error)
    {
        width = 0;
        height = 0;
        sourceWidth = 0;
        bgra = Array.Empty<byte>();
        error = null;

        if (asset.Header.Length < 64 || asset.Raw.Length < 64)
        {
            error = "uiia too small";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(asset.Header.AsSpan(18));
        var loW = BinaryPrimitives.ReadUInt16LittleEndian(asset.Raw.AsSpan(36));
        var loH = BinaryPrimitives.ReadUInt16LittleEndian(asset.Raw.AsSpan(38));
        var hiW = BinaryPrimitives.ReadUInt16LittleEndian(asset.Raw.AsSpan(32));
        var hiH = BinaryPrimitives.ReadUInt16LittleEndian(asset.Raw.AsSpan(34));

        var srcW = loW > 0 ? loW : hiW;
        var srcH = loH > 0 ? loH : hiH;
        sourceWidth = srcW;
        if (srcW < 1 || srcH < 1 || srcW > 8192 || srcH > 8192)
        {
            error = "bad uiia size";
            return false;
        }

        var wb = BlockCount(srcW);
        var hb = BlockCount(srcH);
        var tiles = BuildTileMap(asset.Raw, flags, wb, hb);
        if (tiles is null)
        {
            error = "bad tile table";
            return false;
        }

        // Flatten at native size, then scale. Scaling during the 31px tile blit
        // leaves 1px gutters where dest samples miss both neighboring tiles.
        var fullLen = srcW * srcH * 4;
        var rented = ArrayPool<byte>.Shared.Rent(fullLen);
        var packed = new byte[BlocksPerTile * BlocksPerTile * 16];
        var decoder = new BcDecoder();

        try
        {
            Array.Clear(rented, 0, fullLen);
            for (var ty = 0; ty < hb; ty++)
            {
                for (var tx = 0; tx < wb; tx++)
                {
                    var (op, off) = tiles[ty * wb + tx];
                    CompressionFormat format;
                    int blockBytes;
                    if (op == OpcodeBc7)
                    {
                        format = CompressionFormat.Bc7;
                        blockBytes = 16;
                    }
                    else if (op == OpcodeBc1)
                    {
                        format = CompressionFormat.Bc1;
                        blockBytes = 8;
                    }
                    else
                    {
                        continue;
                    }

                    PackTile(asset.Raw, 64 + off, blockBytes, packed);
                    var colors = decoder.DecodeRaw(packed, TilePx, TilePx, format);
                    BlitTile(colors, rented, srcW, srcH, tx, ty);
                }
            }

            if (maxWidth > 0 && srcW > maxWidth)
            {
                width = maxWidth;
                height = Math.Max(1, (int)((long)srcH * maxWidth / srcW));
                bgra = Downsample(rented, srcW, srcH, width, height);
            }
            else
            {
                width = srcW;
                height = srcH;
                bgra = new byte[fullLen];
                Buffer.BlockCopy(rented, 0, bgra, 0, fullLen);
            }
        }
        catch (Exception ex)
        {
            error = "bc decode: " + ex.Message;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return true;
    }

    public static int BlockCount(int px) => Math.Max(1, (px + 29) / 31);

    private static (byte Opcode, int Offset)[]? BuildTileMap(byte[] raw, ushort flags, int wb, int hb)
    {
        var total = wb * hb;
        var cls = flags & 3;
        var map = new (byte Opcode, int Offset)[total];

        if (cls == 1 || cls == 2)
        {
            var op = cls == 1 ? OpcodeBc1 : OpcodeBc7;
            var size = op == OpcodeBc1 ? 512 : 1024;
            for (var i = 0; i < total; i++)
                map[i] = (op, i * size);
            return map;
        }

        if (raw.Length < 64 + total * 4)
            return null;

        var packed = new uint[total];
        for (var i = 0; i < total; i++)
            packed[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(64 + i * 4));

        for (var i = 0; i < total; i++)
        {
            var opcode = (byte)(packed[i] >> 24);
            var off = (int)(packed[i] & 0xFFFFFF);
            if (opcode == OpcodeCopy)
            {
                if ((uint)off >= (uint)total)
                    return null;
                opcode = (byte)(packed[off] >> 24);
                off = (int)(packed[off] & 0xFFFFFF);
            }
            map[i] = (opcode, off);
        }

        return map;
    }

    private static void PackTile(byte[] raw, int baseOff, int blockBytes, byte[] packed)
    {
        Array.Clear(packed);
        for (var slot = 0; slot < 64; slot++)
        {
            var (bx, by) = s_demorton[slot];
            var src = baseOff + slot * blockBytes;
            if ((uint)src + (uint)blockBytes > (uint)raw.Length)
                continue;
            var dst = (by * BlocksPerTile + bx) * blockBytes;
            raw.AsSpan(src, blockBytes).CopyTo(packed.AsSpan(dst, blockBytes));
        }
    }

    private static void BlitTile(
        ColorRgba32[] tile,
        byte[] dest,
        int destW,
        int destH,
        int tileX,
        int tileY)
    {
        var srcX0 = tileX * TileStride;
        var srcY0 = tileY * TileStride;
        var copyW = Math.Min(TileStride, destW - srcX0);
        var copyH = Math.Min(TileStride, destH - srcY0);
        if (copyW <= 0 || copyH <= 0)
            return;

        for (var y = 0; y < copyH; y++)
        {
            var srcRow = y * TilePx;
            var dstRow = (srcY0 + y) * destW + srcX0;
            for (var x = 0; x < copyW; x++)
            {
                var c = tile[srcRow + x];
                var o = (dstRow + x) * 4;
                dest[o + 0] = c.b;
                dest[o + 1] = c.g;
                dest[o + 2] = c.r;
                dest[o + 3] = c.a;
            }
        }
    }

    private static byte[] Downsample(byte[] src, int srcW, int srcH, int destW, int destH)
    {
        var dest = new byte[destW * destH * 4];
        for (var y = 0; y < destH; y++)
        {
            var y0 = y * srcH / destH;
            var y1 = Math.Max(y0 + 1, (y + 1) * srcH / destH);
            if (y1 > srcH)
                y1 = srcH;
            for (var x = 0; x < destW; x++)
            {
                var x0 = x * srcW / destW;
                var x1 = Math.Max(x0 + 1, (x + 1) * srcW / destW);
                if (x1 > srcW)
                    x1 = srcW;

                long b = 0, g = 0, r = 0, a = 0;
                var n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * srcW;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var o = (row + sx) * 4;
                        b += src[o];
                        g += src[o + 1];
                        r += src[o + 2];
                        a += src[o + 3];
                        n++;
                    }
                }

                var d = (y * destW + x) * 4;
                if (n == 0)
                    continue;
                dest[d] = (byte)(b / n);
                dest[d + 1] = (byte)(g / n);
                dest[d + 2] = (byte)(r / n);
                dest[d + 3] = (byte)(a / n);
            }
        }

        return dest;
    }

    private static (int X, int Y)[] BuildDemorton()
    {
        var table = new (int X, int Y)[64];
        for (var y = 0; y < BlocksPerTile; y++)
        {
            for (var x = 0; x < BlocksPerTile; x++)
                table[Morton2D(x, y)] = (x, y);
        }
        return table;
    }

    private static int Morton2D(int x, int y)
    {
        var m = 0;
        for (var b = 0; b < 3; b++)
        {
            m |= ((x >> b) & 1) << (2 * b);
            m |= ((y >> b) & 1) << (2 * b + 1);
        }
        return m;
    }
}
