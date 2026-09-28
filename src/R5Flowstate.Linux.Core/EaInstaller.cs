using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace R5Flowstate.Linux.Core;

/// <summary>What we know about the installer file on disk.</summary>
public sealed class EaInstallerInfo
{
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string DownloadedUtc { get; set; } = "";
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
}

/// <summary>
/// Official EA App installer: where it lives, how to fetch it, and how to tell a
/// real installer from a CDN error page. The path is unversioned and served as
/// binary/octet-stream, so the bytes are probed (PE header + size floor) before
/// they are accepted — a 404 body must never end up cached as an .exe and run
/// under Proton.
/// </summary>
public static class EaInstaller
{
    /// <summary>Official EA CDN installer (same binary EA links from ea.com).</summary>
    public const string OfficialUrl =
        "https://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAappInstaller.exe";

    /// <summary>Page the download link is published on, used only if the CDN path moves.</summary>
    public const string LandingPageUrl = "https://www.ea.com/ea-app";

    /// <summary>Size of the build measured 2026-09-21. Not enforced — EA rotates
    /// builds — but a mismatch is worth a log line, and it sets the floor.</summary>
    public const long MeasuredSizeBytes = 2_141_216;

    /// <summary>Anything smaller than this is an error page, not an installer.</summary>
    public const long MinimumSizeBytes = 1 << 20;

    public static string DefaultLocalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "EAappInstaller.exe");

    public static string SidecarPathFor(string installerPath) => installerPath + ".json";

    static readonly string[] AllowedHostSuffixes =
    {
        "akamaihd.net",
        "ea.com",
        "eaassets-a.akamaihd.net",
        "origin.com",
    };

    /// <summary>Reads the recorded metadata for an installer already on disk.</summary>
    public static EaInstallerInfo? ReadInfo(string installerPath)
    {
        var sidecar = SidecarPathFor(installerPath);
        try
        {
            if (!File.Exists(sidecar))
                return null;
            return JsonSerializer.Deserialize<EaInstallerInfo>(File.ReadAllText(sidecar));
        }
        catch
        {
            return null;
        }
    }

    static void WriteInfo(string installerPath, EaInstallerInfo info)
    {
        try
        {
            File.WriteAllText(SidecarPathFor(installerPath),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Metadata is a convenience; losing it must not fail the install.
        }
    }

    /// <summary>
    /// Confirms a file is a Windows PE image. The EA installer is served as
    /// binary/octet-stream from a static path, so the only cheap way to know a
    /// transfer was real is to look at it: "MZ" at 0 and the PE signature at
    /// e_lfanew. Returns an error string, or null when the file looks runnable.
    /// </summary>
    public static string? ProbePe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[64];
            var got = fs.Read(head, 0, head.Length);
            if (got < 64)
                return $"{Path.GetFileName(path)} is too short to be a Windows executable ({got} bytes).";
            if (head[0] != (byte)'M' || head[1] != (byte)'Z')
                return $"{Path.GetFileName(path)} does not start with an MZ header — not a Windows executable " +
                       $"(first bytes: {Convert.ToHexString(head, 0, 4)}).";

            var lfanew = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C, 4));
            if (lfanew <= 0 || lfanew > 4096)
                return $"{Path.GetFileName(path)} has an implausible PE header offset (0x{lfanew:X}).";

            fs.Position = lfanew;
            var sig = new byte[4];
            if (fs.Read(sig, 0, 4) < 4 || sig[0] != (byte)'P' || sig[1] != (byte)'E'
                || sig[2] != 0 || sig[3] != 0)
                return $"{Path.GetFileName(path)} is missing the PE signature at 0x{lfanew:X}.";

            return null;
        }
        catch (Exception ex)
        {
            return "Cannot inspect " + Path.GetFileName(path) + ": " + ex.Message;
        }
    }

    /// <summary>True when the body is a real, complete-looking EA installer.</summary>
    public static bool IsUsableInstaller(string path)
        => File.Exists(path)
           && new FileInfo(path).Length >= MinimumSizeBytes
           && ProbePe(path) is null;

    /// <summary>
    /// Pulls the installer URL out of the ea.com landing page. Only used when the
    /// known CDN path stops answering, so a moved build does not require a code
    /// change — the page's anchor is the source of truth EA itself publishes.
    /// </summary>
    public static string? ResolveUrlFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        foreach (Match m in Regex.Matches(html, @"(?:href|src)\s*=\s*[""']([^""']+)[""']",
                     RegexOptions.IgnoreCase))
        {
            var raw = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (raw.Length == 0)
                continue;

            // Parse before matching on the name: published links routinely carry
            // a query string ("…EAappInstaller.exe?utm_source=…"), which a plain
            // EndsWith(".exe") on the raw attribute would miss.
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri.Host))
                continue;
            if (!uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!uri.AbsolutePath.Contains("EAappInstaller", StringComparison.OrdinalIgnoreCase))
                continue;

            // Query and fragment are tracking noise; keep the canonical file URL.
            return uri.GetLeftPart(UriPartial.Path);
        }

        return null;
    }

    static bool IsAllowedHost(string host)
    {
        foreach (var suffix in AllowedHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a usable installer path, downloading it when needed. A cached copy
    /// is validated first, so a previously truncated download is re-fetched
    /// instead of being handed to Proton.
    /// </summary>
    public static async Task<string> EnsureDownloadedAsync(
        Action<string>? log = null,
        IProgress<DownloadProgress>? progress = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var dest = DefaultLocalPath;

        if (!force && IsUsableInstaller(dest))
        {
            var info = ReadInfo(dest);
            log?.Invoke($"EA installer already downloaded ({new FileInfo(dest).Length / 1048576d:0.0} MB" +
                        (info is null ? "" : $", sha256 {Short(info.Sha256)}") + ").");
            return dest;
        }

        if (File.Exists(dest) && !force)
            log?.Invoke("Cached EA installer failed validation — downloading a fresh copy.");

        // Cheap probe of the known path; on anything unexpected, fall back to the
        // landing page so a rotated CDN path is not a hard failure.
        var url = await ResolveUrlAsync(log, ct).ConfigureAwait(false) ?? OfficialUrl;

        log?.Invoke($"Downloading the EA App installer from {url}…");
        DownloadResult result;
        try
        {
            result = await HttpDownload.ToFileAsync(url, dest, new DownloadOptions
            {
                MinBytes = MinimumSizeBytes,
                MaxBytes = 200L << 20,
                ExpectedSha256 = null, // EA publishes no hash; the PE probe is the gate.
                Validate = ProbePe,
                UserAgent = "R5FlowstateLauncher",
            }, progress, log, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not download the EA App installer: {ex.Message}", ex);
        }

        if (!result.Ok)
            throw new InvalidOperationException(
                $"Could not download the EA App installer: {result.Error}");

        if (result.Size != MeasuredSizeBytes && !result.Cached)
            log?.Invoke($"Note: this build is {result.Size} bytes; the build measured 2026-09-21 was " +
                        $"{MeasuredSizeBytes}. EA published a newer installer — continuing.");

        WriteInfo(dest, new EaInstallerInfo
        {
            Url = url,
            Size = result.Size,
            Sha256 = result.Sha256,
            DownloadedUtc = DateTime.UtcNow.ToString("o"),
        });

        log?.Invoke($"Saved EA installer ({result.Size / 1048576d:0.0} MB, sha256 {Short(result.Sha256)})" +
                    (result.Resumed ? " [resumed]" : ""));
        return dest;
    }

    /// <summary>
    /// HEADs the CDN path; if it is not a healthy, size-plausible response, asks
    /// the ea.com page where the installer lives. Returns null to keep the caller
    /// on the built-in URL when nothing better is found.
    /// </summary>
    public static async Task<string?> ResolveUrlAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "R5FlowstateLauncher");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, OfficialUrl);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var declared = resp.Content.Headers.ContentLength;

            // Healthy status and either no declared length or a plausible one.
            if (resp.IsSuccessStatusCode && (declared is null || declared >= MinimumSizeBytes))
                return OfficialUrl;

            log?.Invoke($"EA CDN path answered HTTP {(int)resp.StatusCode}" +
                        (declared is long l ? $" ({l} bytes)" : "") +
                        " — checking ea.com for the current installer link.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"EA CDN probe failed ({ex.Message}) — checking ea.com for the current installer link.");
        }

        try
        {
            var html = await http.GetStringAsync(LandingPageUrl, ct).ConfigureAwait(false);
            var found = ResolveUrlFromHtml(html);
            if (found is not null)
            {
                log?.Invoke($"ea.com lists the installer at {found}");
                return found;
            }
            log?.Invoke("ea.com did not expose an installer link; using the built-in URL.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read {LandingPageUrl} ({ex.Message}); using the built-in URL.");
        }

        return null;
    }

    static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
