using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Resolve CHANNEL_MANIFEST from file://, a local path, or https.
/// Remote hosts: r5flowstate.org and its subdomains, https only. No r2.dev.
/// </summary>
public static class ChannelSource
{
    public const string ChannelUrlEnvVar = "R5F_CHANNEL_URL";
    public const string NotesUrlEnvVar = "R5F_NOTES_URL";

    public static async Task<ChannelManifest> LoadAsync(
        string? configuredUrl,
        IHttpFetcher fetcher,
        string? cacheDir = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        var url = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ChannelUrlEnvVar),
            configuredUrl);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("No CHANNEL URL configured.");

        if (LooksLocal(url))
            return ChannelManifestIO.Load(LocalPath(url));

        EnsureHttpsAllowlisted(url);

        var dest = CachedManifestPath(cacheDir);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            await fetcher.DownloadAsync(url, dest, expectedSha256: null, progress: null, cancel)
                .ConfigureAwait(false);
        }
        catch when (!cancel.IsCancellationRequested && File.Exists(dest))
        {
            // Offline / flaky network: last successfully fetched manifest.
        }

        await VerifySignatureAsync(url, dest, fetcher, cancel).ConfigureAwait(false);

        var man = ChannelManifestIO.Load(dest);
        if (!string.IsNullOrWhiteSpace(man.BaseUrl))
            EnsureHttpsAllowlisted(man.BaseUrl);
        if (!string.IsNullOrWhiteSpace(man.NotesUrl))
        {
            if (IsUncLike(man.NotesUrl) || LooksLocal(man.NotesUrl))
                throw new InvalidOperationException("notes_url on a remote CHANNEL must be https.");
            EnsureHttpsAllowlisted(man.NotesUrl);
        }
        foreach (var tip in new[] { man.Client, man.Server, man.Platform, man.Hd })
        {
            if (tip is null || string.IsNullOrWhiteSpace(tip.CasBaseUrl))
                continue;
            if (IsUncLike(tip.CasBaseUrl) || LooksLocal(tip.CasBaseUrl))
                throw new InvalidOperationException("cas_base_url on a remote CHANNEL must be https.");
            EnsureHttpsAllowlisted(tip.CasBaseUrl);
        }
        return man;
    }

    /// <summary>
    /// The CHANNEL is the root of trust: it pins the manifest by hash, and the
    /// manifest pins every object by hash. Verifying it covers the whole chain.
    /// Inert until a release key is compiled in, so this changes nothing until
    /// signing is adopted.
    /// </summary>
    static async Task VerifySignatureAsync(
        string url, string dest, IHttpFetcher fetcher, CancellationToken cancel)
    {
        if (!ChannelSignature.IsEnforced)
            return;

        var sigPath = dest + ChannelSignature.SidecarSuffix;
        try
        {
            await fetcher.DownloadAsync(
                    ChannelSignature.SidecarUrl(url), sigPath,
                    expectedSha256: null, progress: null, cancel)
                .ConfigureAwait(false);
        }
        catch when (!cancel.IsCancellationRequested)
        {
            // Fall through to the cached sidecar; a missing one is refused below.
        }

        var signature = File.Exists(sigPath) ? File.ReadAllBytes(sigPath) : null;
        var result = ChannelSignature.Verify(File.ReadAllBytes(dest), signature);
        if (result != ChannelSignature.Result.Valid)
        {
            // Refuse rather than fall back to the cached copy: a channel that
            // does not verify is exactly the case this exists to stop.
            throw new InvalidOperationException(
                "Refusing this update channel -- " + ChannelSignature.Describe(result));
        }
    }

    /// <summary>Where LoadAsync caches the fetched manifest for this cacheDir.</summary>
    public static string CachedManifestPath(string? cacheDir) =>
        Path.Combine(
            cacheDir ?? Path.GetTempPath(),
            "r5f-channel",
            ProductConstants.ChannelManifestFileName);

    public static bool LooksLocal(string url) =>
        url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        Path.IsPathRooted(url);

    public static bool IsUncLike(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (url.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.Host))
            return true;
        return false;
    }

    public static string LocalPath(string url)
    {
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var local = Uri.UnescapeDataString(uri.LocalPath);
            if (OperatingSystem.IsWindows() &&
                local.Length >= 3 &&
                local[0] == '/' &&
                char.IsLetter(local[1]) &&
                local[2] == ':')
            {
                local = local[1..];
            }
            return local;
        }
        return url;
    }

    /// <summary>https + r5flowstate.org (or subdomain). Local paths are skipped.</summary>
    public static void EnsureHttpsAllowlisted(string url)
    {
        if (LooksLocal(url))
            return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("URL is not absolute: " + url);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("URL must be https: " + url);
        if (string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException("URL host is empty");
        var host = uri.Host;
        if (host.Equals("r2.dev", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".r2.dev", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("r2.dev is not allowlisted: " + host);
        if (host.Equals("r5flowstate.org", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".r5flowstate.org", StringComparison.OrdinalIgnoreCase))
            return;
        throw new InvalidOperationException("host is not allowlisted: " + host);
    }

    static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }
}
