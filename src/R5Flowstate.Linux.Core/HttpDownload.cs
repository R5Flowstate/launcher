using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace R5Flowstate.Linux.Core;

/// <summary>Byte counters for a running download.</summary>
public readonly record struct DownloadProgress(long Received, long? Total)
{
    /// <summary>0..1 when the server declared a total, else null (indeterminate).</summary>
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;

    public string Describe() => Total is > 0
        ? $"{Mb(Received)}/{Mb(Total.Value)} MB ({Fraction!.Value:P0})"
        : $"{Mb(Received)} MB";

    static string Mb(long bytes) => (bytes / 1048576d).ToString("0.0");
}

/// <summary>Result of a download attempt. Ok is false for every failure mode —
/// bad hash, short body, failed probe, no space — never a silent partial file.</summary>
public sealed class DownloadResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    /// <summary>True when an interrupted .part file was continued rather than restarted.</summary>
    public bool Resumed { get; init; }
    /// <summary>True when an already-valid file on disk was reused (no transfer).</summary>
    public bool Cached { get; init; }

    public static DownloadResult Fail(string error) => new() { Ok = false, Error = error };
}

public sealed class DownloadOptions
{
    /// <summary>Lowercase or upper hex sha256 the finished file must match.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Reject bodies smaller than this. A CDN error page is a few KB.</summary>
    public long MinBytes { get; init; }

    /// <summary>Abort past this size so a runaway or endless response cannot fill the disk.</summary>
    public long MaxBytes { get; init; } = long.MaxValue;

    /// <summary>Continue a pre-existing .part file with a Range request.</summary>
    public bool AllowResume { get; init; } = true;

    /// <summary>Extra attempts after the first failure (resuming where it stopped).</summary>
    public int Retries { get; init; } = 2;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);

    public string UserAgent { get; init; } = "R5FlowstateLauncher";

    /// <summary>Reuse a finished file that already satisfies hash/min/Validate.</summary>
    public bool UseCacheIfValid { get; init; } = true;

    /// <summary>Last check on the downloaded bytes before they are moved into
    /// place (PE header, archive magic). Returns an error string, or null if fine.</summary>
    public Func<string, string?>? Validate { get; init; }
}

/// <summary>
/// Downloads a URL to a file with the guarantees a launcher needs before it
/// hands bytes to Proton: the body is checked against an expected hash and size
/// and optionally probed, and it lands in place atomically. An interrupted or
/// dishonest transfer leaves a reusable .part file, never a plausible-looking
/// final file — a truncated installer must not be cached and later executed.
/// </summary>
public static class HttpDownload
{
    const int BufferBytes = 128 * 1024;

    static HttpClient NewClient(DownloadOptions o)
    {
        var http = new HttpClient { Timeout = o.Timeout };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", o.UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return http;
    }

    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Bytes needed for the transfer plus a small margin, if it can be known.</summary>
    public static long? FreeSpaceFor(string destPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destPath));
            if (string.IsNullOrEmpty(root))
                return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<DownloadResult> ToFileAsync(
        string url,
        string destPath,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var o = options ?? new DownloadOptions();
        var part = destPath + ".part";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            return DownloadResult.Fail($"Refusing non-https download URL: {url}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail("Cannot create download directory: " + ex.Message);
        }

        // A finished file that still passes every check is the cheapest answer.
        if (o.UseCacheIfValid && File.Exists(destPath))
        {
            var cached = await InspectAsync(destPath, o, ct).ConfigureAwait(false);
            if (cached is null)
            {
                var size = new FileInfo(destPath).Length;
                log?.Invoke($"Using existing {Path.GetFileName(destPath)} ({size / 1048576d:0.0} MB).");
                return new DownloadResult
                {
                    Ok = true,
                    Path = destPath,
                    Size = size,
                    Sha256 = o.ExpectedSha256 ?? await Sha256OfFileAsync(destPath, ct).ConfigureAwait(false),
                    Cached = true,
                };
            }
            log?.Invoke($"Existing {Path.GetFileName(destPath)} rejected ({cached}) — downloading again.");
        }

        var start = o.AllowResume && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (start > 0)
            log?.Invoke($"Resuming partial download at {start / 1048576d:0.0} MB.");
        else if (File.Exists(part) && !o.AllowResume)
            TryDelete(part);

        string? lastError = null;
        for (var attempt = 0; attempt <= o.Retries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return DownloadResult.Fail("Download cancelled.");

            try
            {
                var result = await TransferAsync(uri, part, destPath, start, o, progress, log, ct)
                    .ConfigureAwait(false);
                if (result.Ok || !IsRetryable(result.Error) || attempt == o.Retries)
                    return result;

                lastError = result.Error;
                log?.Invoke($"Download failed ({result.Error}) — retrying.");
                start = File.Exists(part) ? new FileInfo(part).Length : 0;
                await Task.Delay(700, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return DownloadResult.Fail("Download cancelled.");
            }
            catch (Exception ex) when (attempt < o.Retries)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                log?.Invoke($"Download failed ({lastError}) — retrying.");
                start = File.Exists(part) ? new FileInfo(part).Length : 0;
                await Task.Delay(700, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return DownloadResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return DownloadResult.Fail(lastError ?? "Download failed.");
    }

    static async Task<DownloadResult> TransferAsync(
        Uri uri,
        string part,
        string destPath,
        long start,
        DownloadOptions o,
        IProgress<DownloadProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        using var http = NewClient(o);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (start > 0)
            req.Headers.Range = new RangeHeaderValue(start, null);

        using var resp = await http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        long resumeFrom;
        bool resumed;
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            // The server honoured the range only if it starts where we asked.
            var from = resp.Content.Headers.ContentRange?.From ?? start;
            resumeFrom = from;
            resumed = from > 0;
        }
        else if (resp.IsSuccessStatusCode)
        {
            // Range ignored (or nothing to resume): start over from zero.
            resumeFrom = 0;
            resumed = false;
        }
        else
        {
            return DownloadResult.Fail($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        long? total = resp.Content.Headers.ContentRange?.Length
                      ?? (resp.Content.Headers.ContentLength is long len ? len + resumeFrom : null);

        // Fail before writing 564 MB onto a full disk.
        if (total is long wanted)
        {
            var free = FreeSpaceFor(destPath);
            if (free is long available && available < wanted - resumeFrom + (64L << 20))
            {
                return DownloadResult.Fail(
                    $"Not enough free space: need {wanted / 1048576d:0} MB, " +
                    $"{available / 1048576d:0} MB available on {Path.GetPathRoot(Path.GetFullPath(destPath))}.");
            }
            if (wanted > o.MaxBytes)
                return DownloadResult.Fail($"Remote file is {wanted} bytes, over the {o.MaxBytes} byte limit.");
        }

        var mode = resumed ? FileMode.Append : FileMode.Create;
        long received = resumeFrom;

        await using (var fs = new FileStream(part, mode, FileAccess.Write, FileShare.None,
                         BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[BufferBytes];
            var lastReported = received;
            int read;
            while ((read = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (received > o.MaxBytes)
                {
                    TryDelete(part);
                    return DownloadResult.Fail($"Download exceeded the {o.MaxBytes} byte limit — aborted.");
                }

                if (progress is not null && received - lastReported >= (1 << 20))
                {
                    lastReported = received;
                    progress.Report(new DownloadProgress(received, total));
                }
            }

            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        progress?.Report(new DownloadProgress(received, total ?? received));

        if (received < o.MinBytes)
        {
            TryDelete(part);
            return DownloadResult.Fail(
                $"Downloaded body is only {received} bytes (expected at least {o.MinBytes}) — discarded.");
        }

        var inspect = await InspectAsync(part, o, ct).ConfigureAwait(false);
        if (inspect is not null)
        {
            // Never leave a failing body behind: it would be picked up as a
            // resume source or, worse, mistaken for a finished download.
            TryDelete(part);
            return DownloadResult.Fail(inspect);
        }

        var sha = o.ExpectedSha256 ?? await Sha256OfFileAsync(part, ct).ConfigureAwait(false);
        File.Move(part, destPath, overwrite: true);

        return new DownloadResult
        {
            Ok = true,
            Path = destPath,
            Size = received,
            Sha256 = sha.ToLowerInvariant(),
            Resumed = resumed,
        };
    }

    /// <summary>Checks a file against the expected hash, size floor and probe.
    /// Returns an error string, or null when the file is good.</summary>
    static async Task<string?> InspectAsync(string path, DownloadOptions o, CancellationToken ct)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            return "Cannot read " + path + ": " + ex.Message;
        }

        if (size < o.MinBytes)
            return $"{Path.GetFileName(path)} is {size} bytes, below the {o.MinBytes} byte minimum.";

        if (!string.IsNullOrWhiteSpace(o.ExpectedSha256))
        {
            var actual = await Sha256OfFileAsync(path, ct).ConfigureAwait(false);
            if (!string.Equals(actual, o.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"sha256 mismatch on {Path.GetFileName(path)}: " +
                       $"expected {o.ExpectedSha256.ToLowerInvariant()}, got {actual}.";
            }
        }

        return o.Validate?.Invoke(path);
    }

    static bool IsRetryable(string? error)
        => error is not null
           && !error.StartsWith("Download cancelled", StringComparison.Ordinal)
           && !error.StartsWith("Refusing", StringComparison.Ordinal)
           && !error.Contains("sha256 mismatch", StringComparison.Ordinal)
           && !error.Contains("Not enough free space", StringComparison.Ordinal)
           && !error.Contains("byte limit", StringComparison.Ordinal)
           && !error.Contains("below the", StringComparison.Ordinal);

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A leftover .part is harmless — it just cannot be trusted as a base.
        }
    }
}
