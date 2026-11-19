using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using R5Flowstate.Content.Rpak;
using R5Flowstate.Linux.Core;
using SkiaSharp;

namespace R5Flowstate.Shell.Linux;

/// <summary>One read's outcome: the picture, or why there is none. The reason is
/// carried, not thrown away — "no decoded art cached" and "cache file is version 2"
/// are different answers and the log says which one it got.</summary>
sealed record ArtResult(IImage? Art, string Reason, string? SourcePath)
{
    public bool Ok => Art is not null;

    public static ArtResult None(string reason) => new(null, reason, null);
}

/// <summary>
/// The loadscreen art lane: <c>&lt;map&gt;_loadscreen.rpak</c> (or a standalone
/// <c>loadscreen_*.rpak</c>) as an Avalonia image for the hero pane and the map
/// tiles.
///
/// <para><b>This class no longer decodes anything.</b> It reads
/// <see cref="ArtCache"/> files that <c>r5f-arthost.exe</c> wrote inside the
/// prefix, and turns their BGRA into a Bitmap. Retail paks are Oodle-encoded and
/// the decoder is a PE DLL a native Linux process cannot load, so the old
/// in-process <see cref="LoadscreenResolver.TryDecode"/> call could never have
/// worked for them on this platform — it is <see cref="ArtDecode"/> that now owns
/// the decode, in the one process where the DLL loads. What is left here is the
/// platform half: BGRA to a Bitmap, a bounded cache, and the off-thread read.</para>
///
/// <para>Ported from the Windows shell's MainWindow.Loadscreen.cs, where the same
/// job was done with WPF's WriteableBitmap; BGRA through Skia is the same decode
/// shape BlogImageLoader uses for blog covers.</para>
///
/// <para>The batch is explicit — <see cref="EnsureCachedAsync"/> runs the host for
/// everything that is missing at one width, and only then does the caller read.
/// One Proton round trip per burst of pictures, rather than one per picture.</para>
/// </summary>
static class LoadscreenArt
{
    /// <summary>Matches the 196px map tile. Do not decode a hero just to shrink it.</summary>
    public const int ThumbWidth = 196;

    /// <summary>The hero pane is ~1000px at the default window.</summary>
    public const int HeroWidth = LoadscreenResolver.DisplayMaxWidth;

    /// <summary>One playlist plus a leftover mode; 196px BGRA is ~90 KB each.</summary>
    const int CacheCap = 24;

    /// <summary>Bitmaps already turned from cache files this session, so flipping
    /// back to a map does not re-read and re-encode its picture.</summary>
    static readonly ConcurrentDictionary<string, Task<ArtResult>> s_cache = new(StringComparer.Ordinal);

    /// <summary>Whether the hosted decoder is installed. When it is not, a retail
    /// map has no art and the reason is worth printing instead of a blank pane.</summary>
    public static bool HostAvailable => ArtDecode.FindHost() is not null;

    /// <summary>
    /// Decode everything asked for that is not already cached, in one run of the
    /// art host. The caller awaits this and then reads
    /// <see cref="ForMapAsync"/>/<see cref="ForPakAsync"/>, which are cache reads.
    /// A missing host, a missing Proton build or a refused prefix is a note and no
    /// art — never an exception.
    /// </summary>
    public static Task<ArtBatch> EnsureCachedAsync(string protonDir, string installRoot,
        int maxWidth, IEnumerable<string>? stems = null, IEnumerable<string>? paks = null,
        Action<string>? log = null, CancellationToken token = default)
    {
        var request = new ArtRequest(
            protonDir ?? string.Empty,
            installRoot ?? string.Empty,
            maxWidth,
            stems is null ? null : new List<string>(stems),
            paks is null ? null : new List<string>(paks),
            Log: log);

        return ArtDecode.EnsureCachedAsync(request, token);
    }

    /// <summary>The map's own loadscreen, read from the cache. <paramref name="maxWidth"/>
    /// picks the lane: <see cref="ThumbWidth"/> for a tile, <see cref="HeroWidth"/>
    /// for the hero.</summary>
    public static Task<ArtResult> ForMapAsync(string mapStem, int maxWidth)
    {
        var stem = mapStem?.Trim() ?? string.Empty;
        if (stem.Length == 0)
            return Task.FromResult(ArtResult.None("no map selected"));

        return Cached(ArtDecode.CacheFileForStem(maxWidth, stem));
    }

    /// <summary>One named art pak, straight — the lane the lobby's stub borrows
    /// and what the shuffle button re-rolls through.</summary>
    public static Task<ArtResult> ForPakAsync(string pakPath, int maxWidth)
    {
        var path = pakPath?.Trim() ?? string.Empty;
        if (path.Length == 0)
            return Task.FromResult(ArtResult.None("no loadscreen pak"));

        return Cached(ArtDecode.CacheFileForPak(maxWidth, path));
    }

    /// <summary>
    /// One cache file as a picture.
    ///
    /// <para>Rejected entries are not retried in a loop: a cache file that is
    /// truncated or of another version stays that way until the host rewrites it,
    /// and the caller's next <see cref="EnsureCachedAsync"/> is what asks for
    /// that. The reason travels so the log can say which one it was.</para>
    ///
    /// <para><b>A failure is not remembered.</b> "No decoded art cached" is a fact
    /// about this moment, not about the picture: a superseded batch keeps decoding
    /// after its caller has walked away (<see cref="ArtDecode"/> does not kill it),
    /// so the file this read did not find can appear a second later. Holding that
    /// miss for the session would make the file invisible to every later click —
    /// art that is on disk, decoded, and never shown. A *success* is remembered,
    /// which is the half worth having: flipping back to a map should not re-read
    /// and re-encode it.</para>
    /// </summary>
    static Task<ArtResult> Cached(string cachePath)
    {
        // A rail click rebuilds the tiles, so a stale cache is worth dropping
        // wholesale rather than growing; the images the panes still show are
        // referenced by the panes themselves and stay alive.
        if (s_cache.Count >= CacheCap * 2)
            s_cache.Clear();

        var task = s_cache.GetOrAdd(cachePath, path => Task.Run(() =>
        {
            if (!ArtCache.TryRead(path, out var pixels, out var error) || pixels is null)
                return ArtResult.None(error ?? "no decoded art cached");

            var art = ToImage(pixels);
            return art is null
                ? ArtResult.None("the cached pixels would not make a bitmap")
                : new ArtResult(art, string.Empty, path);
        }));

        // The pair overload, so a newer entry for the same path is never dropped by
        // an older read finishing late.
        _ = task.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && !t.Result.Ok)
                    s_cache.TryRemove(new KeyValuePair<string, Task<ArtResult>>(cachePath, t));
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return task;
    }

    /// <summary>Loadscreens are RGBA rows (Bgra8888, straight alpha: the decoder
    /// leaves the block decoder's own alpha in place, so Premul would darken an
    /// edge that is merely soft). PNG in between because Avalonia's Bitmap wants
    /// an encoded stream and Skia is already the renderer underneath.</summary>
    internal static IImage? ToImage(LoadscreenPixels pixels)
    {
        var png = ToPng(pixels);
        if (png is null)
            return null;

        try
        {
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The same conversion, stopping one step short of Avalonia.
    ///
    /// <para>Split out because <c>new Bitmap(...)</c> needs the platform's render
    /// interface, which a probe lane does not have and has no business starting —
    /// so without this the read-only probes could not exercise the pixels the pane
    /// will actually be handed, and a cache full of unreadable files would look
    /// exactly like a cache full of pictures. <see cref="BlogImageLoader"/> splits
    /// the same way for the same reason.</para>
    /// </summary>
    internal static byte[]? ToPng(LoadscreenPixels pixels)
    {
        try
        {
            if (pixels.Width <= 0 || pixels.Height <= 0)
                return null;
            var expected = pixels.Width * pixels.Height * 4;
            if (pixels.Bgra.Length < expected)
                return null;

            var info = new SKImageInfo(
                pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var surface = new SKBitmap(info);
            Marshal.Copy(pixels.Bgra, 0, surface.GetPixels(), expected);

            using var image = SKImage.FromBitmap(surface);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null || data.Size == 0)
                return null;

            return data.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the bitmaps this session turned, so a re-install's art is
    /// not served from pictures of the old one. The panes' own references keep
    /// what is on screen alive.</summary>
    public static void Clear() => s_cache.Clear();
}
