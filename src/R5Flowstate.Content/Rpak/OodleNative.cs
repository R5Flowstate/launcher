using System.Runtime.InteropServices;

namespace R5Flowstate.Content.Rpak;

/// <summary>
/// Optional Oodle decode for retail paks (PAK_HEADER_FLAGS_OODLE_ENCODED).
/// Loads oo2core_8_win64.dll only from the launcher folder. Missing DLL
/// means only uncompressed paks work.
/// </summary>
internal static class OodleNative
{
    private const string DllName = "oo2core_8_win64.dll";

    private static readonly object s_gate = new();
    private static IntPtr s_module;
    private static DecompressFn? s_decompress;
    private static bool s_tried;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long DecompressFn(
        IntPtr compBuf, long compBufSize,
        IntPtr rawBuf, long rawLen,
        int fuzzSafe, int checkCrc, int verbosity,
        IntPtr decBufBase, long decBufSize,
        IntPtr fpCallback, IntPtr callbackUserData,
        IntPtr decoderMemory, long decoderMemorySize,
        int threadPhase);

    public static bool IsAvailable
    {
        get
        {
            Ensure();
            return s_decompress is not null;
        }
    }

    public static bool TryDecompress(
        byte[] compressed, int compressedOffset, int compressedLength,
        byte[] raw, int rawOffset, int rawLength)
    {
        Ensure();
        if (s_decompress is null ||
            compressedLength <= 0 || rawLength <= 0 ||
            compressedOffset < 0 || rawOffset < 0 ||
            (long)compressedOffset + compressedLength > compressed.Length ||
            (long)rawOffset + rawLength > raw.Length)
            return false;

        var srcHandle = GCHandle.Alloc(compressed, GCHandleType.Pinned);
        var dstHandle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try
        {
            var n = s_decompress(
                srcHandle.AddrOfPinnedObject() + compressedOffset, compressedLength,
                dstHandle.AddrOfPinnedObject() + rawOffset, rawLength,
                fuzzSafe: 1, checkCrc: 0, verbosity: 0,
                IntPtr.Zero, 0,
                IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, 0,
                threadPhase: 3);
            return n == rawLength;
        }
        finally
        {
            srcHandle.Free();
            dstHandle.Free();
        }
    }

    public static void HintSearchPath(string? directory)
    {
        Ensure();
    }

    public static void EnsureLoaded()
    {
        Ensure();
    }

    private static void Ensure()
    {
        if (s_tried)
            return;
        lock (s_gate)
        {
            if (s_tried)
                return;
            s_tried = true;

            var here = AppContext.BaseDirectory;
            TryLoad(Path.Combine(here, DllName));
            if (s_decompress is not null)
                return;

            TryLoad(Path.Combine(here, "native", DllName));
        }
    }

    private static void TryLoad(string path)
    {
        if (s_decompress is not null || !File.Exists(path))
            return;

        var mod = NativeLibrary.Load(path);
        if (mod == IntPtr.Zero)
            return;

        if (!NativeLibrary.TryGetExport(mod, "OodleLZ_Decompress", out var fn) || fn == IntPtr.Zero)
        {
            NativeLibrary.Free(mod);
            return;
        }

        s_module = mod;
        s_decompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(fn);
    }
}
