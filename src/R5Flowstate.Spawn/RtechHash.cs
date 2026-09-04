using System.Numerics;
using System.Text;

namespace R5Flowstate.Spawn;

/// <summary>
/// rtech HashName / StringToGuid (hashfunc.cpp). Case-folds ASCII, maps '\' to '/',
/// stops at first NUL. Aligned dword path; same result as the unaligned guts.
/// Loc file keys are this u64 printed as lowercase hex (no fixed width).
/// </summary>
public static class RtechHash
{
    /// <summary>HashName64 for a C string (ASCII).</summary>
    public static ulong HashName64(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return 0;

        var bytes = Encoding.ASCII.GetBytes(s);
        // Pad with NULs so every dword read is in-bounds; always at least one trailing NUL dword.
        var pad = 4 - (bytes.Length % 4);
        if (pad == 0)
            pad = 4;
        var buf = new byte[bytes.Length + pad];
        Buffer.BlockCopy(bytes, 0, buf, 0, bytes.Length);

        ulong v1 = 0;
        var i = 0;
        while (true)
        {
            var chunk = BitConverter.ToUInt32(buf, i);

            var v4 = (~chunk & (chunk - 0x01010101u) & 0x80808080u);
            var v5 = v4 ^ (v4 - 1u);
            var v6 = (v5 & chunk) ^ 0x5C5C5C5Cu;
            var v7 = (~v6 & (v6 - 0x01010101u) & 0x80808080u);
            var v8 = v7 & unchecked((uint)-(int)v7);
            if (v7 != v8)
            {
                var v9 = 0xFF000000u;
                while (v9 >= 0x100u)
                {
                    if ((v9 & v6) == 0)
                        v8 |= v9 & 0x80808080u;
                    v9 >>= 8;
                }
            }

            var v11 = 0x633D5F1UL * v1;
            var masked = ((v5 & chunk) - 45u * (v8 >> 7)) & 0xDFDFDFDFu;
            var v12 = (0xFB8C4D96501UL * masked) >> 24;

            if (v4 != 0)
            {
                var bsr = BitScanReverse(v5);
                return v12 + v11 - 0xAE502812AA7333UL * (uint)(i + bsr / 8);
            }

            i += 4;
            var combined = v11 + v12;
            v1 = (combined >> 61) ^ combined;
        }
    }

    /// <summary>Lowercase hex key as written in localization_*.txt (no fixed width).</summary>
    public static string ToLocHexKey(ulong hash) =>
        hash.ToString("x");

    /// <summary>Strip leading # then HashName64 hex key.</summary>
    public static string LocKeyFromToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;
        var t = token.Trim();
        if (t.StartsWith('#'))
            t = t[1..];
        if (t.Length == 0)
            return string.Empty;
        return ToLocHexKey(HashName64(t));
    }

    private static int BitScanReverse(uint value)
    {
        if (value == 0)
            return -1;
        return 31 - BitOperations.LeadingZeroCount(value);
    }
}
