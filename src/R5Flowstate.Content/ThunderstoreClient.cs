using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Thunderstore community client for r5flowstate. Host check is independent of
/// ChannelSource.EnsureHttpsAllowlisted -- thunderstore.io is not on the CAS allowlist.
/// </summary>
public sealed class ThunderstoreClient : IDisposable
{
    public const string Community = "r5flowstate";
    public const string DefaultBaseUrl = "https://thunderstore.io";

    public const int MaxIndexBytes = 1 * 1024 * 1024;
    public const int MaxIndexDecodedBytes = 2 * 1024 * 1024;
    public const int MaxPackageListBytes = 32 * 1024 * 1024;
    public const int MaxPackageListDecodedBytes = 64 * 1024 * 1024;
    public const int MaxProfileBytes = 2 * 1024 * 1024;
    public const int MaxProfileDecodedBytes = 8 * 1024 * 1024;
    public const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxRedirects = 5;

    static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly string _indexBase;
    readonly bool _indexIsLocal;

    public ThunderstoreClient(string? indexBaseUrl = null)
        : this(indexBaseUrl, CreateHttp(), ownsHttp: true)
    {
    }

    public ThunderstoreClient(string? indexBaseUrl, HttpMessageHandler handler)
        : this(indexBaseUrl, CreateHttp(handler), ownsHttp: true)
    {
    }

    ThunderstoreClient(string? indexBaseUrl, HttpClient http, bool ownsHttp)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
        _indexBase = string.IsNullOrWhiteSpace(indexBaseUrl)
            ? DefaultBaseUrl
            : indexBaseUrl.Trim();
        _indexIsLocal = ChannelSource.LooksLocal(_indexBase);
    }

    static HttpClient CreateHttp(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        if (handler is HttpClientHandler httpHandler)
            httpHandler.AllowAutoRedirect = false;

        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "R5FlowstateLauncher/0.1");
        return http;
    }

    /// <summary>https only; host is thunderstore.io or a subdomain. Local paths skip this.</summary>
    public static void EnsureThunderstoreHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("URL is not absolute: " + url);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("URL must be https: " + url);
        if (string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("URL host is empty");
        var host = uri.Host;
        if (host.Equals("thunderstore.io", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase))
            return;
        throw new InvalidOperationException("host is not thunderstore.io: " + host);
    }

    void EnsureLiveCatalog()
    {
        if (!ProductConstants.ThunderstoreEnabled)
            throw new InvalidOperationException("Thunderstore catalog is disabled.");
    }

    public async Task<IReadOnlyList<ModPackage>> ListPackagesAsync(CancellationToken cancel = default)
    {
        if (_indexIsLocal)
            return await ListLocalAsync(cancel).ConfigureAwait(false);

        EnsureLiveCatalog();

        Exception? indexError = null;
        try
        {
            return await ListViaIndexAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            indexError = ex;
        }

        try
        {
            return await ListViaPackageEndpointAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Thunderstore listing failed (index: " +
                (indexError?.Message ?? "n/a") +
                "; package list: " + ex.Message + ").",
                ex);
        }
    }

    public async Task<string> CreateProfileAsync(ModProfile profile, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_indexIsLocal)
            throw new InvalidOperationException("Profile create needs the Thunderstore host, not a local index.");
        EnsureLiveCatalog();

        var body = EncodeProfile(profile);
        var url = CombineUrl(_indexBase, "api/experimental/legacyprofile/create/");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(ApiTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        EnsureThunderstoreHost(url);
        using var response = await SendNoRedirectAsync(request, cts.Token).ConfigureAwait(false);
        var bytes = await ReadCappedAsync(
            response, MaxProfileBytes, MaxProfileDecodedBytes, cts.Token).ConfigureAwait(false);
        EnsureSuccess(response, bytes);

        var json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
            {
                var s = key.GetString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }

            if (doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                var s = code.GetString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        throw new InvalidOperationException("Thunderstore profile create returned no key.");
    }

    public async Task<ModProfile> GetProfileAsync(string code, CancellationToken cancel = default)
    {
        if (!IsSafeProfileCode(code))
            throw new ArgumentException("Invalid profile code.", nameof(code));
        if (_indexIsLocal)
            throw new InvalidOperationException("Profile get needs the Thunderstore host, not a local index.");
        EnsureLiveCatalog();

        var url = CombineUrl(_indexBase, "api/experimental/legacyprofile/get/" + code + "/");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(ApiTimeout);
        var bytes = await GetFollowAsync(
            url, MaxProfileBytes, MaxProfileDecodedBytes, cts.Token).ConfigureAwait(false);
        return DecodeProfile(bytes);
    }

    public async Task DownloadAsync(
        string url,
        string destPath,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(destPath))
            throw new ArgumentException("Destination path is required.", nameof(destPath));

        if (ChannelSource.LooksLocal(url))
        {
            var local = ChannelSource.LocalPath(url);
            await CopyLocalAsync(local, destPath, progress, cancel).ConfigureAwait(false);
            return;
        }

        if (Path.IsPathRooted(url) && File.Exists(url))
        {
            await CopyLocalAsync(url, destPath, progress, cancel).ConfigureAwait(false);
            return;
        }

        EnsureLiveCatalog();

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = destPath + ".partial";
        try
        {
            using var response = await GetFinalResponseAsync(url, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength ?? 0;
            if (declared > MaxDownloadBytes)
                throw new InvalidOperationException("Download exceeds size cap.");

            await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var output = new FileStream(
                tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);

            var buffer = new byte[1024 * 1024];
            long done = 0;
            var fileName = Path.GetFileName(destPath);
            progress?.Report(new ContentInstallProgress
            {
                Phase = "download",
                Unit = ProgressUnit.Bytes,
                FileName = fileName,
                Message = fileName,
                Current = 0,
                Total = declared,
            });

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stall.CancelAfter(StallTimeout);
            var lastReport = DateTime.UtcNow;
            while (true)
            {
                int read;
                try
                {
                    read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new IOException($"Stalled for {StallTimeout.TotalSeconds:0}s: {fileName}");
                }

                if (read <= 0)
                    break;

                stall.CancelAfter(StallTimeout);
                done += read;
                if (done > MaxDownloadBytes)
                    throw new InvalidOperationException("Download exceeds size cap.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                if (done >= declared || (now - lastReport).TotalMilliseconds >= 200)
                {
                    lastReport = now;
                    progress?.Report(new ContentInstallProgress
                    {
                        Phase = "download",
                        Unit = ProgressUnit.Bytes,
                        FileName = fileName,
                        Message = fileName,
                        Current = done,
                        Total = declared > 0 ? declared : done,
                    });
                }
            }

            File.Move(tmp, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    async Task<IReadOnlyList<ModPackage>> ListViaIndexAsync(CancellationToken cancel)
    {
        var url = CombineUrl(_indexBase, "c/" + Community + "/api/v1/package-listing-index/");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(ApiTimeout);
        var bytes = await GetFollowAsync(
            url, MaxIndexBytes, MaxIndexDecodedBytes, cts.Token).ConfigureAwait(false);
        var parsed = ParseIndexOrPackages(bytes);
        if (parsed.Packages is not null)
            return Normalize(parsed.Packages);
        if (parsed.ChunkUrls is null || parsed.ChunkUrls.Count == 0)
            return Array.Empty<ModPackage>();
        if (parsed.ChunkUrls.Count > 64)
            throw new InvalidOperationException("Thunderstore index listed too many chunks.");

        var all = new List<ModPackage>();
        foreach (var chunk in parsed.ChunkUrls)
        {
            cancel.ThrowIfCancellationRequested();
            using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            chunkCts.CancelAfter(ApiTimeout);
            var chunkBytes = await FetchBytesAsync(
                chunk, localDir: null, MaxPackageListBytes, MaxPackageListDecodedBytes, chunkCts.Token)
                .ConfigureAwait(false);
            var chunkParsed = ParseIndexOrPackages(chunkBytes);
            if (chunkParsed.Packages is not null)
                all.AddRange(chunkParsed.Packages);
        }

        return Normalize(all);
    }

    async Task<IReadOnlyList<ModPackage>> ListViaPackageEndpointAsync(CancellationToken cancel)
    {
        var url = CombineUrl(_indexBase, "c/" + Community + "/api/v1/package/");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var bytes = await GetFollowAsync(
            url, MaxPackageListBytes, MaxPackageListDecodedBytes, cts.Token).ConfigureAwait(false);
        var parsed = ParseIndexOrPackages(bytes);
        if (parsed.Packages is null)
            throw new InvalidOperationException("Thunderstore /package/ did not return a package array.");
        return Normalize(parsed.Packages);
    }

    async Task<IReadOnlyList<ModPackage>> ListLocalAsync(CancellationToken cancel)
    {
        var path = ChannelSource.LocalPath(_indexBase);
        if (Directory.Exists(path))
        {
            var probe = Path.Combine(path, "package-listing-index.json");
            if (!File.Exists(probe))
                probe = Path.Combine(path, "index.json");
            if (!File.Exists(probe))
                probe = Path.Combine(path, "packages.json");
            if (!File.Exists(probe))
                throw new FileNotFoundException("Local Thunderstore index file not found.", path);
            path = probe;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException("Local Thunderstore index file not found.", path);

        var bytes = MaybeDecompress(
            await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false),
            MaxIndexDecodedBytes);
        var parsed = ParseIndexOrPackages(bytes);
        if (parsed.Packages is not null)
            return Normalize(parsed.Packages);

        if (parsed.ChunkUrls is null || parsed.ChunkUrls.Count == 0)
            return Array.Empty<ModPackage>();
        if (parsed.ChunkUrls.Count > 64)
            throw new InvalidOperationException("Thunderstore index listed too many chunks.");

        var localDir = Path.GetDirectoryName(path);
        var all = new List<ModPackage>();
        foreach (var chunk in parsed.ChunkUrls)
        {
            cancel.ThrowIfCancellationRequested();
            var chunkBytes = await FetchBytesAsync(
                chunk, localDir, MaxPackageListBytes, MaxPackageListDecodedBytes, cancel)
                .ConfigureAwait(false);
            var chunkParsed = ParseIndexOrPackages(chunkBytes);
            if (chunkParsed.Packages is not null)
                all.AddRange(chunkParsed.Packages);
        }

        return Normalize(all);
    }

    async Task<byte[]> FetchBytesAsync(
        string url,
        string? localDir,
        int maxBytes,
        int maxDecoded,
        CancellationToken cancel)
    {
        if (ChannelSource.LooksLocal(url) || LooksRelativeFile(url))
        {
            var path = ResolveLocalChunk(url, localDir);
            var raw = await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false);
            if (raw.Length > maxBytes)
                throw new InvalidOperationException("Local chunk exceeds size cap.");
            return MaybeDecompress(raw, maxDecoded);
        }

        return await GetFollowAsync(url, maxBytes, maxDecoded, cancel).ConfigureAwait(false);
    }

    static bool LooksRelativeFile(string url) =>
        !url.Contains("://", StringComparison.Ordinal) && !Path.IsPathRooted(url);

    static string ResolveLocalChunk(string url, string? localDir)
    {
        if (ChannelSource.LooksLocal(url))
            return ChannelSource.LocalPath(url);
        if (localDir is null)
            throw new InvalidOperationException("Relative chunk URL with no index directory.");
        if (!SafePath.TryJoin(localDir, url, out var full))
            throw new InvalidOperationException("Refusing chunk path outside the index directory: " + url);
        return full;
    }

    async Task<byte[]> GetFollowAsync(string url, int maxBytes, int maxDecoded, CancellationToken cancel)
    {
        using var response = await GetFinalResponseAsync(url, cancel).ConfigureAwait(false);
        var bytes = await ReadCappedAsync(response, maxBytes, maxDecoded, cancel).ConfigureAwait(false);
        EnsureSuccess(response, bytes);
        return bytes;
    }

    async Task<HttpResponseMessage> GetFinalResponseAsync(string url, CancellationToken cancel)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("URL is not absolute: " + url);

        HttpResponseMessage? response = null;
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            EnsureThunderstoreHost(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            response = await SendNoRedirectAsync(request, cancel).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is >= 300 and < 400)
            {
                var loc = response.Headers.Location;
                response.Dispose();
                response = null;
                if (loc is null)
                    throw new InvalidOperationException("redirect without Location");
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                continue;
            }

            EnsureThunderstoreHost(uri.AbsoluteUri);
            return response;
        }

        response?.Dispose();
        throw new InvalidOperationException("too many redirects");
    }

    Task<HttpResponseMessage> SendNoRedirectAsync(HttpRequestMessage request, CancellationToken cancel) =>
        _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);

    static async Task<byte[]> ReadCappedAsync(
        HttpResponseMessage response,
        int maxBytes,
        int maxDecoded,
        CancellationToken cancel)
    {
        var declared = response.Content.Headers.ContentLength ?? 0;
        if (declared > maxBytes)
            throw new InvalidOperationException("Response exceeds size cap.");

        await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var ms = new MemoryStream(declared > 0 ? (int)Math.Min(declared, maxBytes) : 0);
        var buffer = new byte[81920];
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        while (true)
        {
            int read;
            try
            {
                read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new IOException($"Stalled for {StallTimeout.TotalSeconds:0}s reading response.");
            }

            if (read <= 0)
                break;
            stall.CancelAfter(StallTimeout);
            if (ms.Length + read > maxBytes)
                throw new InvalidOperationException("Response exceeds size cap.");
            ms.Write(buffer, 0, read);
        }

        return MaybeDecompress(ms.ToArray(), maxDecoded);
    }

    static byte[] MaybeDecompress(byte[] raw, int maxDecoded)
    {
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B)
            return GunzipCapped(raw, maxDecoded);
        if (raw.Length > maxDecoded)
            throw new InvalidOperationException("Response exceeds decompressed size cap.");
        return raw;
    }

    static byte[] GunzipCapped(byte[] compressed, int maxUncompressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxUncompressed)
                throw new InvalidOperationException("gzip payload exceeds size cap.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    static void EnsureSuccess(HttpResponseMessage response, byte[] body)
    {
        if (response.IsSuccessStatusCode)
            return;
        var snippet = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 200));
        throw new InvalidOperationException(
            "HTTP " + (int)response.StatusCode + " from Thunderstore: " + snippet);
    }

    static IndexParse ParseIndexOrPackages(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
                return new IndexParse(new List<ModPackage>(), null);

            var first = root[0];
            if (first.ValueKind == JsonValueKind.String)
            {
                var urls = new List<string>();
                foreach (var el in root.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String)
                        continue;
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s))
                        urls.Add(s);
                }

                return new IndexParse(null, urls);
            }

            return new IndexParse(DeserializePackages(json), null);
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            {
                var urls = new List<string>();
                foreach (var el in chunks.EnumerateArray())
                {
                    var s = ChunkUrlOf(el);
                    if (!string.IsNullOrEmpty(s))
                        urls.Add(s);
                }

                return new IndexParse(null, urls);
            }

            foreach (var name in new[] { "full_url", "full_index", "url" })
            {
                if (root.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.String)
                {
                    var s = u.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return new IndexParse(null, new List<string> { s });
                }
            }

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                return new IndexParse(DeserializePackages(results.GetRawText()), null);
        }

        throw new InvalidOperationException("Thunderstore index was not a package array or chunk list.");
    }

    static string? ChunkUrlOf(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("url", out var u) &&
            u.ValueKind == JsonValueKind.String)
        {
            return u.GetString();
        }

        return null;
    }

    static List<ModPackage> DeserializePackages(string json)
    {
        var list = JsonSerializer.Deserialize<List<ModPackage>>(json, s_json);
        return list ?? new List<ModPackage>();
    }

    static IReadOnlyList<ModPackage> Normalize(IReadOnlyList<ModPackage> packages)
    {
        var result = new List<ModPackage>(packages.Count);
        foreach (var p in packages)
        {
            var versions = p.Versions ?? Array.Empty<ModPackageVersion>();
            var owner = p.Owner ?? string.Empty;
            var name = p.Name ?? string.Empty;
            var full = p.FullName ?? string.Empty;
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name))
                SplitFullName(full, ref owner, ref name);

            var desc = p.Description ?? string.Empty;
            var icon = p.IconUrl ?? string.Empty;
            if (versions.Count > 0)
            {
                var latest = versions[0];
                if (string.IsNullOrEmpty(desc))
                    desc = latest.Description ?? string.Empty;
                if (string.IsNullOrEmpty(icon))
                    icon = latest.Icon ?? string.Empty;
            }

            result.Add(new ModPackage
            {
                FullName = full,
                Owner = owner,
                Name = name,
                Description = desc,
                IconUrl = icon,
                IsDeprecated = p.IsDeprecated,
                IsNsfw = p.IsNsfw,
                Categories = p.Categories ?? Array.Empty<string>(),
                Versions = versions,
            });
        }

        return result;
    }

    static void SplitFullName(string full, ref string owner, ref string name)
    {
        var dash = full.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash >= full.Length - 1)
            return;
        if (string.IsNullOrEmpty(owner))
            owner = full[..dash];
        if (string.IsNullOrEmpty(name))
            name = full[(dash + 1)..];
    }

    static string EncodeProfile(ModProfile profile)
    {
        var yaml = new StringBuilder();
        foreach (var pin in profile.Packages ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(pin))
                continue;
            yaml.Append("- ").Append(pin.Trim()).Append('\n');
        }

        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            writer.Write(yaml.ToString());
        }

        return "#r2modman\n" + Convert.ToBase64String(ms.ToArray());
    }

    static ModProfile DecodeProfile(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                text = data.GetString() ?? text;
            else if (doc.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                text = body.GetString() ?? text;
        }

        const string prefix = "#r2modman";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            text = text[prefix.Length..];
            if (text.StartsWith('\r'))
                text = text[1..];
            if (text.StartsWith('\n'))
                text = text[1..];
        }

        text = text.Trim();
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Profile body was not base64.", ex);
        }

        var yamlBytes = MaybeDecompress(decoded, MaxProfileDecodedBytes);
        var yaml = Encoding.UTF8.GetString(yamlBytes);
        var pins = new List<string>();
        using var reader = new StringReader(yaml);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                trimmed = trimmed[2..].Trim();
            else if (trimmed.StartsWith('-'))
                trimmed = trimmed[1..].Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.Contains(':', StringComparison.Ordinal))
                continue;
            pins.Add(trimmed.Trim('\'', '"'));
        }

        return new ModProfile { Packages = pins };
    }

    static bool IsSafeProfileCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128)
            return false;
        foreach (var c in code)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                continue;
            return false;
        }

        return true;
    }

    static string CombineUrl(string baseUrl, string relative)
    {
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        return new Uri(new Uri(baseUrl), relative).AbsoluteUri;
    }

    static async Task CopyLocalAsync(
        string src,
        string dest,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var fileName = Path.GetFileName(src);
        progress?.Report(new ContentInstallProgress
        {
            Phase = "download",
            Unit = ProgressUnit.Bytes,
            FileName = fileName,
            Message = fileName,
        });
        await using var input = new FileStream(
            src, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var tmp = dest + ".partial";
        try
        {
            await using (var output = new FileStream(
                             tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await input.CopyToAsync(output, cancel).ConfigureAwait(false);
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    readonly record struct IndexParse(List<ModPackage>? Packages, List<string>? ChunkUrls);
}
