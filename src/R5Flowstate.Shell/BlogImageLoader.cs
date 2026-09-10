using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace R5Flowstate.Shell;

/// <summary>
/// WPF cannot decode WebP and its Image downloader sends no User-Agent.
/// Fetch with the launcher UA, decode (including WebP), hand back a BitmapImage.
/// </summary>
static class BlogImageLoader
{
    const int MaxBytes = 8 * 1024 * 1024;
    const int MaxEdge = 1280;

    static readonly HttpClient s_http;
    static readonly ConcurrentDictionary<string, Task<BitmapImage?>> s_cache = new(StringComparer.Ordinal);

    static BlogImageLoader()
    {
        s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "R5Flowstate/0.1");
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "image/webp,image/png,image/jpeg,image/*;q=0.8");
    }

    public static Task<BitmapImage?> GetAsync(Uri url)
    {
        return s_cache.GetOrAdd(url.AbsoluteUri, _ => LoadCore(url));
    }

    public static async void Bind(
        System.Windows.Controls.Image img,
        Uri? url,
        System.Windows.Controls.TextBlock? placeholder = null)
    {
        var token = url?.AbsoluteUri ?? "";
        img.Tag = token;
        if (url is null)
        {
            img.Source = null;
            img.Visibility = System.Windows.Visibility.Collapsed;
            if (placeholder is not null)
                placeholder.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        BitmapImage? bmp;
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
        img.Visibility = bmp is null
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        if (placeholder is not null)
        {
            placeholder.Visibility = bmp is null
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }
    }

    static async Task<BitmapImage?> LoadCore(Uri url)
    {
        if (!HostOk(url))
            return null;

        byte[] raw;
        try
        {
            using var resp = await s_http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            raw = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (raw.Length == 0 || raw.Length > MaxBytes)
            return null;

        byte[]? png;
        try
        {
            png = await Task.Run(() => ToPng(raw)).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (png is null || png.Length == 0)
            return null;

        try
        {
            return CreateBitmap(png);
        }
        catch
        {
            return null;
        }
    }

    static bool HostOk(Uri url)
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
        using var image = Image.Load(raw);
        if (image.Width > MaxEdge || image.Height > MaxEdge)
        {
            image.Mutate(c => c.Resize(new ResizeOptions
            {
                Size = new Size(MaxEdge, MaxEdge),
                Mode = ResizeMode.Max,
            }));
        }
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.Level1 });
        return ms.ToArray();
    }

    static BitmapImage CreateBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(png);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
