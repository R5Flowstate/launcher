using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Load NOTES.json from the CHANNEL sibling / notes_url, plus the launcher nupkg copy.
/// Never r5fms.
/// </summary>
public static class NotesSource
{
    public static async Task<List<NotesEntry>> LoadAsync(
        ChannelManifest? channel,
        string? channelUrl,
        IHttpFetcher fetcher,
        string? cacheDir = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        var rows = new List<NotesEntry>();

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(ChannelSource.NotesUrlEnvVar),
            channel?.NotesUrl,
            SiblingNotesUrl(channelUrl),
            SiblingNotesUrl(channel?.BaseUrl),
        };
        foreach (var cand in candidates)
        {
            var got = await TryFetchAsync(cand, fetcher, cacheDir, "game", cancel)
                .ConfigureAwait(false);
            if (got.Count == 0)
                continue;
            Append(rows, got);
            break;
        }

        if (rows.Count == 0)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, ProductConstants.NotesFileName);
            if (File.Exists(bundled))
                Append(rows, NotesDocumentIO.Load(bundled).Entries);
        }

        return rows;
    }

    /// <summary>Launcher changelog: launcher-feed/NOTES.json, else the bundled copy.</summary>
    public static async Task<List<NotesEntry>> LoadLauncherAsync(
        IHttpFetcher fetcher,
        string? cacheDir = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        var rows = new List<NotesEntry>();

        var url = ProductConstants.DefaultLauncherFeedUrl + ProductConstants.NotesFileName;
        Append(rows, await TryFetchAsync(url, fetcher, cacheDir, "launcher", cancel)
            .ConfigureAwait(false));

        return MergeLauncherNotes(rows, LoadBundled(ProductConstants.LauncherNotesFileName));
    }

    /// <summary>Cached game notes, else the bundled NOTES.json. Never network.</summary>
    public static List<NotesEntry> LoadGameCachedOrBundled(string? cacheDir) =>
        LoadCachedOrBundled(CachePath(cacheDir, "game"), ProductConstants.NotesFileName);

    /// <summary>Cached launcher notes merged with the bundled copy. Never network.</summary>
    public static List<NotesEntry> LoadLauncherCachedOrBundled(string? cacheDir) =>
        MergeLauncherNotes(
            LoadCachedOrBundled(CachePath(cacheDir, "launcher"), bundledFileName: null),
            LoadBundled(ProductConstants.LauncherNotesFileName));

    static List<NotesEntry> LoadCachedOrBundled(string cachePath, string? bundledFileName)
    {
        try
        {
            if (File.Exists(cachePath))
                return NotesDocumentIO.Load(cachePath).Entries;
            if (!string.IsNullOrWhiteSpace(bundledFileName))
                return LoadBundled(bundledFileName);
        }
        catch
        {
            // fall through
        }
        return new List<NotesEntry>();
    }

    static List<NotesEntry> LoadBundled(string fileName)
    {
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(bundled))
                return NotesDocumentIO.Load(bundled).Entries;
        }
        catch
        {
            // fall through
        }
        return new List<NotesEntry>();
    }

    // CDN/cache can lag a local ship. Keep bundled entries that the cache
    // does not have, in bundled order, in front of the overlap.
    static List<NotesEntry> MergeLauncherNotes(
        List<NotesEntry> remote,
        List<NotesEntry> bundled)
    {
        if (bundled.Count == 0)
            return remote;
        if (remote.Count == 0)
            return bundled;

        var remoteTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in remote)
        {
            if (!string.IsNullOrWhiteSpace(e.Title))
                remoteTitles.Add(e.Title.Trim());
        }

        var prefix = new List<NotesEntry>();
        foreach (var e in bundled)
        {
            var title = (e.Title ?? string.Empty).Trim();
            if (title.Length > 0 && remoteTitles.Contains(title))
                break;
            prefix.Add(e);
        }

        if (prefix.Count == 0)
            return remote;

        var dest = new List<NotesEntry>(prefix.Count + remote.Count);
        dest.AddRange(prefix);
        dest.AddRange(remote);
        return dest;
    }

    static string CachePath(string? cacheDir, string cacheName) =>
        Path.Combine(
            cacheDir ?? Path.GetTempPath(),
            "r5f-notes",
            cacheName + "-" + ProductConstants.NotesFileName);

    static async Task<List<NotesEntry>> TryFetchAsync(
        string? url,
        IHttpFetcher fetcher,
        string? cacheDir,
        string cacheName,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new List<NotesEntry>();

        var dest = CachePath(cacheDir, cacheName);
        try
        {
            if (ChannelSource.LooksLocal(url))
            {
                if (ChannelSource.IsUncLike(url))
                    return new List<NotesEntry>();
                var path = ChannelSource.LocalPath(url);
                if (!File.Exists(path))
                    return new List<NotesEntry>();
                return NotesDocumentIO.Load(path).Entries;
            }

            ChannelSource.EnsureHttpsAllowlisted(url);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await fetcher.DownloadAsync(url, dest, expectedSha256: null, progress: null, cancel)
                .ConfigureAwait(false);
            if (!File.Exists(dest))
                return new List<NotesEntry>();
            return NotesDocumentIO.Load(dest).Entries;
        }
        catch
        {
            // Offline / flaky network: last successfully fetched copy.
            try
            {
                if (File.Exists(dest))
                    return NotesDocumentIO.Load(dest).Entries;
            }
            catch
            {
                // corrupt cache -- treat as absent
            }
            return new List<NotesEntry>();
        }
    }

    static string? SiblingNotesUrl(string? channelUrl)
    {
        if (string.IsNullOrWhiteSpace(channelUrl))
            return null;

        if (ChannelSource.LooksLocal(channelUrl))
        {
            var path = ChannelSource.LocalPath(channelUrl);
            if (Directory.Exists(path))
                return Path.Combine(path, ProductConstants.NotesFileName);
            var dir = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(dir)
                ? null
                : Path.Combine(dir, ProductConstants.NotesFileName);
        }

        if (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var uri))
            return null;
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            return new Uri(uri, ProductConstants.NotesFileName).ToString();
        return new Uri(uri, ProductConstants.NotesFileName).ToString();
    }

    static void Append(List<NotesEntry> dest, List<NotesEntry>? src)
    {
        if (src is null)
            return;
        foreach (var e in src)
        {
            if (e is null || e.Items is null || e.Items.Count == 0)
                continue;
            dest.Add(e);
        }
    }

}
