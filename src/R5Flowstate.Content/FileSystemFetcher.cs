using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Fetches https://, file://, and absolute local paths into destPath.
/// Skips re-download when dest exists and matches expectedSha256.
/// </summary>
public sealed class FileSystemFetcher : IHttpFetcher, IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsHttp;

    static long s_maxBytesPerSecond;

    /// <summary>0 = unlimited. Read per chunk, so a change applies mid-download.</summary>
    public static long GlobalMaxBytesPerSecond
    {
        get => Interlocked.Read(ref s_maxBytesPerSecond);
        set => Interlocked.Exchange(ref s_maxBytesPerSecond, Math.Max(0, value));
    }

    public InstallRunControl? RunControl { get; set; }

    /// <summary>Abort a content read that delivers no bytes for this long.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public FileSystemFetcher()
        : this(CreateHttp(null), ownsHttp: true)
    {
    }

    /// <summary>
    /// Bounded-timeout fetcher for small metadata (channel/notes). HttpClient's
    /// timeout covers the body read too, so this ctor is only safe for small
    /// documents; content downloads use the default ctor plus StallTimeout.
    /// </summary>
    public FileSystemFetcher(TimeSpan timeout)
        : this(CreateHttp(timeout), ownsHttp: true)
    {
    }

    static HttpClient CreateHttp(TimeSpan? timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Recycle pooled sockets so a resume after sleep does not inherit a
            // dead connection or a stale DNS answer.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // We hash the bytes as they sit in object storage, so the transport
            // must not transform them.
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 32,
        };
        var http = new HttpClient(handler)
        {
            // HTTP/1.1 on purpose. Measured against this CDN, one keep-alive
            // connection reaches the same rate as sixteen, so multiplexing buys
            // nothing here and H2 flow control is one more thing to get wrong.
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            // A whole-request timeout would cap how slow a legitimate multi-GB
            // download may be. Stalls are caught by StallTimeout instead.
            Timeout = timeout ?? Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", ProductConstants.ProductName + "/0.1");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        return http;
    }

    public FileSystemFetcher(HttpClient http, bool ownsHttp = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
    }

    public async Task DownloadAsync(
        string url,
        string destPath,
        string? expectedSha256 = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));
        if (string.IsNullOrWhiteSpace(destPath))
            throw new ArgumentException("Destination path is required.", nameof(destPath));
        if (RunControl is not null)
            await RunControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            File.Exists(destPath) &&
            await Sha256MatchesAsync(destPath, expectedSha256, cancel).ConfigureAwait(false))
        {
            progress?.Report(new ContentInstallProgress
            {
                Phase = "download",
                Message = $"skip (sha256 match): {Path.GetFileName(destPath)}",
            });
            return;
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string? computed;
        if (TryGetLocalPath(url, out var localSrc))
        {
            computed = await CopyLocalAsync(localSrc, destPath, progress, cancel).ConfigureAwait(false);
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                 uri.Scheme == Uri.UriSchemeHttps)
        {
            ChannelSource.EnsureHttpsAllowlisted(uri.AbsoluteUri);
            computed = await DownloadHttpAsync(uri, destPath, progress, cancel).ConfigureAwait(false);
        }
        else if (Path.IsPathRooted(url) && File.Exists(url))
        {
            computed = await CopyLocalAsync(url, destPath, progress, cancel).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported fetch URL (need https://, file://, or absolute local path): {url}");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            // The write loop hashes as it goes; only re-read when it could not
            // (a resumed transfer whose earlier bytes this process never saw).
            var ok = computed is not null
                ? string.Equals(computed, NormalizeHex(expectedSha256), StringComparison.Ordinal)
                : await Sha256MatchesAsync(destPath, expectedSha256, cancel).ConfigureAwait(false);
            if (!ok)
            {
                TryDeleteFile(destPath);
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {Path.GetFileName(destPath)} (expected {expectedSha256}).");
            }
        }
    }

    static bool TryGetLocalPath(string url, out string localPath)
    {
        localPath = string.Empty;

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return false;
            localPath = Uri.UnescapeDataString(fileUri.LocalPath);
            if (OperatingSystem.IsWindows() &&
                localPath.Length >= 3 &&
                localPath[0] == '/' &&
                char.IsLetter(localPath[1]) &&
                localPath[2] == ':')
            {
                localPath = localPath[1..];
            }
            return File.Exists(localPath);
        }

        if (Path.IsPathRooted(url) && File.Exists(url))
        {
            localPath = url;
            return true;
        }

        if (!Path.IsPathRooted(url))
        {
            var rel = Path.GetFullPath(url);
            if (File.Exists(rel))
            {
                localPath = rel;
                return true;
            }
        }

        return false;
    }

    async Task<string?> CopyLocalAsync(
        string src,
        string dest,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        var fileName = Path.GetFileName(src);
        progress?.Report(new ContentInstallProgress
        {
            Phase = "download",
            FileName = fileName,
            Message = fileName,
        });

        var tmp = dest + ".partial";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var input = new FileStream(
                             src, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            await using (var output = new FileStream(
                             tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long total = input.Length;
                long done = 0;
                int read;
                var lastReport = DateTime.UtcNow;
                ReportCopy(progress, fileName, 0, total);
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancel)
                           .ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (RunControl is not null)
                        await RunControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    if (done >= total || (now - lastReport).TotalMilliseconds >= 200)
                    {
                        lastReport = now;
                        ReportCopy(progress, fileName, done, total);
                    }
                }
                ReportCopy(progress, fileName, done, total);
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }

        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>Total wall clock a single object may spend retrying.</summary>
    public static TimeSpan RetryBudget { get; set; } = TimeSpan.FromMinutes(10);

    sealed class TransientHttpException : Exception
    {
        public TransientHttpException(string message, TimeSpan? retryAfter)
            : base(message) => RetryAfter = retryAfter;

        public TimeSpan? RetryAfter { get; }
    }

    async Task<string?> DownloadHttpAsync(
        Uri uri,
        string dest,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        return await RetryAsync(
            uri,
            () => DownloadHttpOnceAsync(uri, dest, progress, cancel),
            cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// One retry policy for every transfer: exponential backoff with full
    /// jitter, bounded by elapsed time rather than an attempt count, so a slow
    /// link is never by itself a reason a download stops.
    /// </summary>
    async Task<T> RetryAsync<T>(Uri uri, Func<Task<T>> attemptOnce, CancellationToken cancel)
    {
        Exception? last = null;
        var started = DateTime.UtcNow;
        var rng = new Random(uri.GetHashCode() ^ Environment.TickCount);

        for (var attempt = 0; ; attempt++)
        {
            if (RunControl is not null)
                await RunControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            try
            {
                return await attemptOnce().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex, cancel))
            {
                last = ex;
                if (DateTime.UtcNow - started > RetryBudget)
                    break;

                // Jitter keeps a fleet of parallel workers from resynchronising
                // onto the same retry instant.
                var ceiling = Math.Min(30_000d, 500d * Math.Pow(2, Math.Min(attempt, 6)));
                var delay = TimeSpan.FromMilliseconds(rng.NextDouble() * ceiling);
                if (ex is TransientHttpException { RetryAfter: { } after } && after > delay)
                    delay = after;

                await WaitForNetworkAsync(cancel).ConfigureAwait(false);
                await Task.Delay(delay, cancel).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException("Download failed.");
    }

    /// <summary>
    /// Fetch one content-addressed object into memory and verify it before the
    /// caller writes a byte, so a part file never holds bytes already known to
    /// be wrong. A cache-buster on retry is the only way past a poisoned edge
    /// copy of an immutable object.
    /// </summary>
    public async Task<byte[]> GetObjectAsync(
        string url,
        string expectedSha256,
        long expectedLength,
        CancellationToken cancel = default,
        Action<long>? onBytes = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Bad object URL: {url}");

        if (!TryGetLocalPath(uri.AbsoluteUri, out _))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Remote object URL must be https: {url}");
            ChannelSource.EnsureHttpsAllowlisted(uri.AbsoluteUri);
        }

        var want = NormalizeHex(expectedSha256);
        for (var pass = 0; pass < 3; pass++)
        {
            var target = uri;
            if (pass > 0)
            {
                var sep = uri.Query.Length > 0 ? "&" : "?";
                target = new Uri(uri + sep + "cb=" + Guid.NewGuid().ToString("N"));
            }

            byte[] bytes = TryGetLocalPath(target.AbsoluteUri, out var local)
                ? await File.ReadAllBytesAsync(local, cancel).ConfigureAwait(false)
                : await RetryAsync(
                        target,
                        () => GetObjectOnceAsync(target, expectedLength, cancel, onBytes),
                        cancel)
                    .ConfigureAwait(false);

            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (string.Equals(actual, want, StringComparison.Ordinal))
                return bytes;

            if (LooksLikeHtml(bytes))
            {
                throw new InvalidOperationException(
                    "The network returned a web page instead of game content; a captive " +
                    "portal or content filter is intercepting the download.");
            }
        }

        throw new InvalidOperationException(
            $"Object {want[..12]} failed verification even after a cache-busted retry; " +
            "the copy being served is wrong.");
    }

    async Task<byte[]> GetObjectOnceAsync(
        Uri uri, long expectedLength, CancellationToken cancel, Action<long>? onBytes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);

        var status = (int)response.StatusCode;
        if (status == 429 || status >= 500)
            throw new TransientHttpException($"HTTP {status} fetching an object", RetryAfterOf(response));
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength ?? 0;
        if (expectedLength > 0 && declared > 0 && declared != expectedLength)
            throw new IOException($"Object length {declared}, expected {expectedLength}.");

        var cap = expectedLength > 0 ? expectedLength : declared;
        using var buffer = new MemoryStream(cap > 0 ? (int)Math.Min(cap, int.MaxValue) : 0);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        {
            var chunk = new byte[81920];
            stall.CancelAfter(StallTimeout);
            while (true)
            {
                int read;
                try
                {
                    read = await input.ReadAsync(chunk.AsMemory(), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new IOException($"Stalled for {StallTimeout.TotalSeconds:0}s fetching an object.");
                }
                if (read <= 0)
                    break;
                if (expectedLength > 0 && buffer.Length + read > expectedLength)
                    throw new IOException($"Object exceeds expected length {expectedLength}.");
                stall.CancelAfter(StallTimeout);
                buffer.Write(chunk, 0, read);
                onBytes?.Invoke(read);
                if (RunControl is not null)
                    await RunControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);
            }
        }

        if (expectedLength > 0 && buffer.Length != expectedLength)
            throw new IOException($"Object truncated: {buffer.Length} of {expectedLength} bytes.");

        return buffer.ToArray();
    }

    static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        TimeSpan? after = null;
        if (ra?.Delta is { } d)
            after = d;
        else if (ra?.Date is { } when)
            after = when - DateTimeOffset.UtcNow;
        if (after is { } a && (a < TimeSpan.Zero || a > TimeSpan.FromMinutes(5)))
            after = null;
        return after;
    }

    /// <summary>A filter page returned with a 200 is a network problem, not a corrupt file.</summary>
    static bool LooksLikeHtml(byte[] bytes)
    {
        var n = Math.Min(bytes.Length, 512);
        for (var i = 0; i + 5 < n; i++)
        {
            if (bytes[i] == 0x3C &&
                (bytes[i + 1] | 0x20) == 0x68 &&
                (bytes[i + 2] | 0x20) == 0x74 &&
                (bytes[i + 3] | 0x20) == 0x6D &&
                (bytes[i + 4] | 0x20) == 0x6C)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A cancelled read is only fatal when the caller asked for it. Anything
    /// else -- a stall abort, a dropped socket, a 5xx -- is worth another go.
    /// </summary>
    static bool IsRetryable(Exception ex, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
            return false;
        return ex is IOException or HttpRequestException or TransientHttpException
            or OperationCanceledException;
    }

    /// <summary>Park instead of burning retries while the machine is offline.</summary>
    static async Task WaitForNetworkAsync(CancellationToken cancel)
    {
        while (!NetworkInterface.GetIsNetworkAvailable())
        {
            cancel.ThrowIfCancellationRequested();
            await Task.Delay(2000, cancel).ConfigureAwait(false);
        }
    }

    static string NormalizeHex(string hex) =>
        hex.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    async Task<string?> DownloadHttpOnceAsync(
        Uri uri,
        string dest,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        progress?.Report(new ContentInstallProgress
        {
            Phase = "download",
            Message = $"http {Path.GetFileName(dest)}",
        });

        var tmp = dest + ".partial";
        long existing = 0;
        if (File.Exists(tmp))
        {
            try { existing = new FileInfo(tmp).Length; }
            catch { existing = 0; }
        }

        HttpResponseMessage? response = null;
        for (var hop = 0; hop < 5; hop++)
        {
            ChannelSource.EnsureHttpsAllowlisted(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (existing > 0 && hop == 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

            response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is >= 300 and < 400)
            {
                var loc = response.Headers.Location
                    ?? throw new InvalidOperationException("redirect without Location");
                response.Dispose();
                response = null;
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                existing = 0;
                continue;
            }
            break;
        }
        if (response is null)
            throw new InvalidOperationException("too many redirects");
        using (response)
        {

        var resumed = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (existing > 0 && !resumed)
        {
            try { File.Delete(tmp); } catch { /* restart */ }
            existing = 0;
        }

        var status = (int)response.StatusCode;
        if (status == 429 || status >= 500)
        {
            TimeSpan? after = null;
            var ra = response.Headers.RetryAfter;
            if (ra?.Delta is { } d)
                after = d;
            else if (ra?.Date is { } when)
                after = when - DateTimeOffset.UtcNow;
            if (after is { } a && (a < TimeSpan.Zero || a > TimeSpan.FromMinutes(5)))
                after = null;
            throw new TransientHttpException(
                $"HTTP {status} for {Path.GetFileName(dest)}", after);
        }

        response.EnsureSuccessStatusCode();

        long contentLen = response.Content.Headers.ContentLength ?? 0;
        long total = resumed ? existing + contentLen : contentLen;
        var fileMode = resumed ? FileMode.Append : FileMode.Create;

        // A resumed transfer never saw the leading bytes, so it cannot produce a
        // whole-file hash; the caller falls back to re-reading in that case.
        using var hash = resumed ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Guards a socket that accepts but never delivers. HttpClient.Timeout
        // cannot do this job without also capping how slow a link may be.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var output = new FileStream(
                             tmp, fileMode, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long done = existing;
                int read;
                var fileName = Path.GetFileName(dest);
                var lastReport = DateTime.UtcNow;
                var throttle = new ThrottleWindow();
                ReportCopy(progress, fileName, done, total);

                stall.CancelAfter(StallTimeout);
                while (true)
                {
                    try
                    {
                        read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        throw new IOException(
                            $"Stalled for {StallTimeout.TotalSeconds:0}s: {fileName}");
                    }
                    if (read <= 0)
                        break;
                    stall.CancelAfter(StallTimeout);

                    await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    hash?.AppendData(buffer, 0, read);
                    done += read;
                    if (RunControl is not null)
                        await RunControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);
                    await throttle.PaceAsync(read, cancel).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    if (done >= total || (now - lastReport).TotalMilliseconds >= 200)
                    {
                        lastReport = now;
                        ReportCopy(progress, fileName, done, total);
                    }
                }

                if (total > 0 && done < total)
                    throw new IOException(
                        $"Truncated download for {fileName}: got {done} of {total} bytes.");

                ReportCopy(progress, fileName, done, total);
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            // Keep .partial so the next attempt can Range-resume.
            throw;
        }

        return hash is null
            ? null
            : Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Sleeps whenever bytes arrive ahead of the cap. The window resets every
    /// few seconds so a slow stretch does not bank credit for a later burst.
    /// </summary>
    sealed class ThrottleWindow
    {
        DateTime _startUtc = DateTime.UtcNow;
        long _bytes;

        public async Task PaceAsync(int read, CancellationToken cancel)
        {
            var cap = GlobalMaxBytesPerSecond;
            if (cap <= 0)
            {
                _bytes = 0;
                _startUtc = DateTime.UtcNow;
                return;
            }

            _bytes += read;
            var elapsed = (DateTime.UtcNow - _startUtc).TotalSeconds;
            var budget = _bytes / (double)cap;
            if (budget > elapsed)
            {
                var wait = Math.Min(1.0, budget - elapsed);
                await Task.Delay(TimeSpan.FromSeconds(wait), cancel).ConfigureAwait(false);
                elapsed += wait;
            }

            if (elapsed >= 4)
            {
                _startUtc = DateTime.UtcNow;
                _bytes = 0;
            }
        }
    }

    static void ReportCopy(
        IProgress<ContentInstallProgress>? progress,
        string fileName,
        long done,
        long total)
    {
        progress?.Report(new ContentInstallProgress
        {
            Phase = "download",
            FileName = fileName,
            Message = fileName,
            Current = done,
            Total = total,
        });
    }

    public static async Task<bool> Sha256MatchesAsync(
        string path,
        string expectedHex,
        CancellationToken cancel = default)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(expectedHex))
            return false;

        var expected = expectedHex.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
