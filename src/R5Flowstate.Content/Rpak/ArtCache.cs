namespace R5Flowstate.Content.Rpak;

/// <summary>
/// The on-disk form of one decoded loadscreen, and the rule that names it.
///
/// <para><b>Why a cache file exists at all.</b> Retail paks are Oodle-encoded and
/// the decoder is <c>oo2core_8_win64.dll</c> — a PE DLL, which a native Linux
/// process cannot load (glibc's <c>dlopen</c> answers "invalid ELF header"). The
/// Windows shell decodes in-process; on Linux the decode therefore has to happen
/// in the one place the DLL does load, which is a Windows process inside the
/// Proton prefix. That process writes the pixels here and the launcher reads them back, so the
/// expensive half runs once per picture instead of once per frame — and a
/// picker that is reopened is instant.</para>
///
/// <para>The format is deliberately the decoder's own output, not an image
/// format: <see cref="LoadscreenPixels.Bgra"/> in file order behind a small
/// header. Nothing on either side has to own an encoder, and the Linux side is
/// already holding the BGRA-to-bitmap step (<c>LoadscreenArt.ToImage</c>).</para>
///
/// <para>Shared by both sides because it is the same fact twice otherwise: the
/// ArtHost writes these files and the launcher reads them, and the key rule had
/// better be one implementation or a cache hit becomes a coin toss.</para>
/// </summary>
public static class ArtCache
{
    /// <summary>'R5FA'.</summary>
    public const uint Magic = 0x41463552;

    public const int Version = 1;

    /// <summary>magic, version, width, height, sourceWidth, payload length.</summary>
    public const int HeaderSize = 24;

    public const string Extension = ".r5fa";

    /// <summary>The cache file for a map stem. <c>mp_rr_desertlands_hu</c> and
    /// <c>MP_RR_DESERTLANDS_HU</c> are the same map, so the key is folded.</summary>
    public static string KeyForStem(string stem) => Sanitize(stem);

    /// <summary>The cache file for one standalone art pak, keyed by its stem:
    /// the same pak reached by path or by name has to land in one file.</summary>
    public static string KeyForPak(string pakPath)
        => Sanitize(Path.GetFileNameWithoutExtension(pakPath ?? string.Empty));

    public static string FileNameFor(string key) => key + Extension;

    /// <summary>Filesystem-safe and case-folded. Windows' install ships 280
    /// loadscreens whose names are already tame; this is here so an odd one
    /// cannot escape the cache directory.</summary>
    static string Sanitize(string raw)
    {
        var s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0)
            return "unnamed";

        var chars = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_';
        }

        var name = new string(chars);
        // A leading dot would hide the file; ".." would leave the directory.
        return name.TrimStart('.') is { Length: > 0 } trimmed ? trimmed : "unnamed";
    }

    /// <summary>Write one decode. Returns false with a reason rather than throwing:
    /// the caller is a console host whose only channel is its exit code and its
    /// records, and a half-written cache file is worse than a missing one.</summary>
    public static bool Write(string path, LoadscreenPixels pixels, out string? error)
    {
        error = null;
        try
        {
            var expected = pixels.Width * pixels.Height * 4;
            if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.Bgra.Length < expected)
            {
                error = "refusing to write a short pixel buffer";
                return false;
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // A temp file then a move: the launcher reads this directory
            // concurrently with a host that may be writing into it, and a reader
            // must never see a truncated picture.
            var tmp = path + ".part";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(pixels.Width);
                w.Write(pixels.Height);
                w.Write(pixels.SourceWidth > 0 ? pixels.SourceWidth : pixels.Width);
                w.Write(expected);
                w.Write(pixels.Bgra, 0, expected);
            }

            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>One cache file's header, without its pixels.</summary>
    public readonly record struct ArtHeader(int Version, int Width, int Height, int SourceWidth, int Payload);

    /// <summary>
    /// Whether a file is a usable cache entry, reading 24 bytes rather than the
    /// picture.
    ///
    /// <para>This is what the launcher asks before it decides to spend a Proton
    /// round trip on a decode: "is this map's art already here, at this width, in
    /// this version of the format". Reading the payload to answer that would
    /// allocate a multi-megabyte buffer per map only to throw it away, and then
    /// read the same file again to actually draw it.</para>
    /// </summary>
    public static bool TryReadHeader(string path, out ArtHeader header, out string? error)
    {
        header = default;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "no decoded art cached";
                return false;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadHeader(fs, out header, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The header half, on a stream already positioned at the start, so
    /// <see cref="TryRead"/> can validate and then keep reading the same stream
    /// instead of opening the file twice.</summary>
    static bool TryReadHeader(Stream fs, out ArtHeader header, out string? error)
    {
        header = default;
        error = null;

        try
        {
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

            if (fs.Length < HeaderSize)
            {
                error = "cache file is truncated";
                return false;
            }
            if (r.ReadUInt32() != Magic)
            {
                error = "cache file is not R5F art";
                return false;
            }

            var version = r.ReadInt32();
            if (version != Version)
            {
                error = $"cache file is version {version}";
                return false;
            }

            var width = r.ReadInt32();
            var height = r.ReadInt32();
            var sourceWidth = r.ReadInt32();
            var payload = r.ReadInt32();

            if (width <= 0 || height <= 0 || payload != width * height * 4)
            {
                error = "cache header disagrees with itself";
                return false;
            }
            if (fs.Length - HeaderSize < payload)
            {
                error = "cache file is short of pixels";
                return false;
            }

            header = new ArtHeader(version, width, height, sourceWidth, payload);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryRead(string path, out LoadscreenPixels? pixels, out string? error)
    {
        pixels = null;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "no decoded art cached";
                return false;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!TryReadHeader(fs, out var header, out error))
                return false;

            fs.Position = HeaderSize;
            using var r = new BinaryReader(fs);

            var bgra = new byte[header.Payload];
            if (r.Read(bgra, 0, header.Payload) != header.Payload)
            {
                error = "cache file ended mid-picture";
                return false;
            }

            pixels = new LoadscreenPixels
            {
                Width = header.Width,
                Height = header.Height,
                SourceWidth = header.SourceWidth,
                Bgra = bgra,
                SourcePath = string.Empty,
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
