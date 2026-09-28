using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Avalonia's own Bitmap loader sends no User-Agent and, on the desktop
/// back-ends it ships, decodes the formats the blog actually serves — but the
/// site publishes WebP, which Avalonia 11 does not decode at all. So: fetch with
/// the launcher UA, decode with Skia (which does know WebP, and is already the
/// renderer underneath), downscale, re-encode to PNG and hand Avalonia a Bitmap.
/// Ported from the Windows Shell, where the same job was done with ImageSharp
/// because WPF could not decode WebP either.
///
/// Every rule the Windows loader enforces is kept: https only, the three site
/// hosts only, an 8 MB ceiling, a 1280-pixel longest edge, and one in-flight
/// fetch per URL that every caller shares.
/// </summary>
static class BlogImageLoader
{
    const int MaxBytes = 8 * 1024 * 1024;
    const int MaxEdge = 1280;

    static readonly HttpClient s_http;
    static readonly ConcurrentDictionary<string, Task<Bitmap?>> s_cache = new(StringComparer.Ordinal);

    static BlogImageLoader()
    {
        s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "R5Flowstate/0.1");
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "image/webp,image/png,image/jpeg,image/*;q=0.8");
    }

    public static Task<Bitmap?> GetAsync(Uri url)
    {
        return s_cache.GetOrAdd(url.AbsoluteUri, _ => LoadCore(url));
    }

    /// <summary>Fetch, then paint — but only if the control still wants this
    /// image. The Tag carries the URL that was asked for, so a card that has
    /// been reused (or a gallery the reader has clicked past) is never painted
    /// with the answer to somebody else's request. A null URL collapses the
    /// image and shows the placeholder instead; a failed fetch does the same,
    /// which is why the placeholder carries the alt text or the raw URL.</summary>
    public static async void Bind(Image img, Uri? url, TextBlock? placeholder = null)
    {
        var token = url?.AbsoluteUri ?? "";
        img.Tag = token;
        if (url is null)
        {
            img.Source = null;
            img.IsVisible = false;
            if (placeholder is not null)
                placeholder.IsVisible = true;
            return;
        }

        Bitmap? bmp;
        try
        {
            bmp = await GetAsync(url).ConfigureAwait(true);
        }
        catch
        {
            bmp = null;
        }

        if (!string.Equals(img.Tag as string, token, StringComparison.Ordinal))
            return;

        img.Source = bmp;
        img.IsVisible = bmp is not null;
        if (placeholder is not null)
            placeholder.IsVisible = bmp is null;
    }

    static async Task<Bitmap?> LoadCore(Uri url)
    {
        var raw = await FetchAsync(url).ConfigureAwait(false);
        if (raw is null)
            return null;

        var png = await ToPngAsync(raw).ConfigureAwait(false);
        if (png is null || png.Length == 0)
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

    /// <summary>The whole loader except the final Avalonia Bitmap — the fetch and
    /// the decode, reporting the size that came out after the downscale. The
    /// live probe (<c>--blog --images</c>) is the only caller: it is how the one
    /// thing a windowless run cannot see gets checked, namely that the formats
    /// the site actually serves (WebP) decode on this platform at all. Null means
    /// refused by the allow-list, failed, or not an image Skia knows.</summary>
    internal static async Task<(int Width, int Height)?> FetchSizeAsync(Uri url)
    {
        var raw = await FetchAsync(url).ConfigureAwait(false);
        if (raw is null)
            return null;

        var png = await ToPngAsync(raw).ConfigureAwait(false);
        if (png is null || png.Length == 0)
            return null;

        try
        {
            using var bmp = SKBitmap.Decode(png);
            return bmp is null ? null : (bmp.Width, bmp.Height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetch one image: https, the three site hosts, the launcher UA,
    /// a 20 s timeout and an 8 MB ceiling. Everything that decides "is this
    /// even a request we make" happens before the socket.</summary>
    static async Task<byte[]?> FetchAsync(Uri url)
    {
        if (!HostOk(url))
            return null;

        try
        {
            using var resp = await s_http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var raw = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return raw.Length == 0 || raw.Length > MaxBytes ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    static async Task<byte[]?> ToPngAsync(byte[] raw)
    {
        try
        {
            return await Task.Run(() => ToPng(raw)).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The blog's images are only ever the site's own — never a
    /// third-party host a post could point at. Internal only so the live probe
    /// can tell "this post points somewhere we refuse by design" apart from
    /// "this picture would not decode", which are very different answers.</summary>
    internal static bool HostOk(Uri url)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        var host = url.Host;
        return host.Equals("r5flowstate.org", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.r5flowstate.org", StringComparison.OrdinalIgnoreCase)
            || host.Equals("cdn.r5flowstate.org", StringComparison.OrdinalIgnoreCase);
    }

    static byte[]? ToPng(byte[] raw)
    {
        using var decoded = SKBitmap.Decode(raw);
        if (decoded is null)
            return null;

        SKBitmap? scaled = null;
        try
        {
            var src = decoded;
            if (decoded.Width > MaxEdge || decoded.Height > MaxEdge)
            {
                var scale = Math.Min(
                    (double)MaxEdge / decoded.Width,
                    (double)MaxEdge / decoded.Height);
                var w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
                var h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
                scaled = decoded.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
                if (scaled is not null)
                    src = scaled;
            }

            using var image = SKImage.FromBitmap(src);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}
