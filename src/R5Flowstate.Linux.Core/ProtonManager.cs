using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Linux.Core;

/// <summary>The GE-Proton build the release API currently points at.</summary>
public sealed class ProtonGeRelease
{
    public string Tag { get; init; } = "";
    public string AssetName { get; init; } = "";
    public string Url { get; init; } = "";
    /// <summary>sha256 published by GitHub for this asset, when it is offered.</summary>
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    /// <summary>Directory the tarball extracts to (the tarball's top-level folder).</summary>
    public string DirName { get; init; } = "";

    public string Describe() => $"{Tag} ({Size / 1048576d:0} MB" +
                                (Sha256 is null ? ", no published digest" : $", sha256 {Sha256[..12]}") + ")";
}

/// <summary>
/// Ensures a Proton build exists. If Steam ships none, downloads Proton-GE
/// (GloriousEggroll) into compatibilitytools.d. The tarball is checked against the
/// sha256 GitHub publishes for the asset and the extracted tree gets its
/// executable bits, which is what makes the "proton" wrapper script runnable.
///
/// Proton is what runs the game, so a build has to exist before any launch — but
/// the download is ~500 MB, so it is now the player's decision: with
/// <c>allowDownload: false</c> this reports that nothing was found and returns
/// empty, and the caller points at the Download Proton-GE button instead of
/// starting a transfer nobody asked for. (The old "no Wine fallback needed" note
/// predated the split runtime; the fallback for a missing Proton build is now the
/// EA Runtime setting, not a silent download.)
/// </summary>
public static class ProtonManager
{
    public const string ReleaseApiUrl =
        "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases/latest";

    /// <summary>A GE-Proton tarball is ~500 MB; anything under this is not one.</summary>
    public const long MinimumTarballBytes = 64L << 20;

    public static string CompatToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local/share/Steam/compatibilitytools.d");

    /// <summary>Returns usable Proton list, downloading GE-Proton if none found —
    /// unless <paramref name="allowDownload"/> is false, in which case a missing
    /// build is reported and an empty list returned.</summary>
    public static async Task<IReadOnlyList<ProtonEntry>> EnsureProtonAsync(
        Action<string>? log = null,
        IProgress<DownloadProgress>? progress = null,
        bool allowDownload = true,
        CancellationToken ct = default)
    {
        var found = ProtonDiscovery.Discover();
        if (found.Count > 0)
        {
            log?.Invoke($"Proton available: {found[0].Name}");
            return found;
        }

        if (!allowDownload)
        {
            log?.Invoke("No Proton found under Steam, and downloading Proton-GE is "
                        + "switched off in settings.");
            return found;
        }

        log?.Invoke("No Proton found under Steam. Downloading Proton-GE…");
        await DownloadLatestGeProtonAsync(log, progress, ct).ConfigureAwait(false);
        var again = ProtonDiscovery.Discover();
        if (again.Count > 0)
            log?.Invoke($"Proton-GE ready: {again[0].Name}");
        else
            log?.Invoke("Proton-GE install finished but no proton script detected.");
        return again;
    }

    sealed class GeReleaseJson
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("assets")] public List<GeAssetJson> Assets { get; set; } = new();
    }

    sealed class GeAssetJson
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        /// <summary>GitHub publishes "sha256:&lt;hex&gt;" here.</summary>
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    /// <summary>Asks the GitHub releases API for the newest GE-Proton tarball.</summary>
    public static async Task<ProtonGeRelease> ResolveLatestAsync(
        Action<string>? log = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "R5Flowstate-Linux/2.00");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        log?.Invoke("Querying Proton-GE latest release…");
        var rel = await http.GetFromJsonAsync<GeReleaseJson>(ReleaseApiUrl, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("GE release lookup returned nothing.");

        // Prefer the x86_64 build when a release carries more than one tarball.
        var tarballs = rel.Assets
            .Where(a => a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                        && a.Name.Contains("GE-Proton", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var asset = tarballs.FirstOrDefault(a => a.Name.Contains("x86_64", StringComparison.OrdinalIgnoreCase))
                    ?? tarballs.FirstOrDefault()
                    ?? throw new InvalidOperationException("No GE-Proton tarball in the latest release.");

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"GE asset URL is not a GitHub https URL: {asset.Url}");
        }

        var sha = ParseDigest(asset.Digest);
        if (sha is null)
            log?.Invoke("This release publishes no sha256 digest — size and archive checks only.");
        if (asset.Size > 0 && asset.Size < MinimumTarballBytes)
            throw new InvalidOperationException(
                $"GE asset {asset.Name} is only {asset.Size} bytes — refusing to treat it as a Proton build.");

        return new ProtonGeRelease
        {
            Tag = rel.Tag,
            AssetName = asset.Name,
            Url = uri.GetLeftPart(UriPartial.Path),
            Sha256 = sha,
            Size = asset.Size,
            DirName = StripArchiveSuffix(asset.Name),
        };
    }

    /// <summary>"sha256:abc…" → "abc…"; other algorithms and blanks return null.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        var i = digest.IndexOf(':');
        if (i <= 0)
            return null;
        var algo = digest[..i].Trim();
        var hex = digest[(i + 1)..].Trim();
        if (!algo.Equals("sha256", StringComparison.OrdinalIgnoreCase))
            return null;
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            return null;
        return hex.ToLowerInvariant();
    }

    /// <summary>"GE-Proton11-7-x86_64.tar.gz" → "GE-Proton11-7-x86_64".</summary>
    public static string StripArchiveSuffix(string assetName)
    {
        var name = assetName;
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];
        else if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    /// <summary>
    /// Finds an already-installed build for this release, if there is one. The
    /// folder is named by the archive, not the asset: the asset is
    /// "GE-Proton11-7-x86_64.tar.gz" but it extracts to "GE-Proton11-7", so
    /// trusting the asset name alone would re-download 538 MB.
    /// </summary>
    static string? FindInstalled(ProtonGeRelease release)
    {
        var candidates = new List<string>
        {
            Path.Combine(CompatToolsDir, release.DirName),
            Path.Combine(CompatToolsDir, release.Tag),
            Path.Combine(CompatToolsDir, release.DirName.Replace("-x86_64", "")),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "proton")))
                return candidate;
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(CompatToolsDir))
            {
                var name = Path.GetFileName(dir);
                if (!name.Contains(release.Tag, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(Path.Combine(dir, "proton")))
                    return dir;
            }
        }
        catch
        {
            // No compatibilitytools.d yet — nothing is installed.
        }

        return null;
    }

    /// <summary>
    /// Downloads and extracts the newest GE-Proton. Returns the directory holding
    /// the "proton" script. An already-extracted, still-runnable build is reused
    /// rather than re-downloading half a gigabyte.
    /// </summary>
    public static async Task<string> DownloadLatestGeProtonAsync(
        Action<string>? log = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var release = await ResolveLatestAsync(log, ct).ConfigureAwait(false);

        var installed = FindInstalled(release);
        if (installed is not null)
        {
            log?.Invoke($"GE-Proton {release.Tag} is already installed at {installed}.");
            return installed;
        }

        var finalDir = Path.Combine(CompatToolsDir, release.DirName);
        log?.Invoke($"Downloading {release.AssetName} — {release.Describe()}");
        Directory.CreateDirectory(CompatToolsDir);

        var tarPath = Path.Combine(CompatToolsDir, release.AssetName);
        var result = await HttpDownload.ToFileAsync(tarPath, tarPath, new DownloadOptions
        {
            ExpectedSha256 = release.Sha256,
            MinBytes = MinimumTarballBytes,
            MaxBytes = 8L << 30,
            UserAgent = "R5Flowstate-Linux/2.00",
            // A gzip member starts with 0x1F 0x8B; an error page does not.
            Validate = ProbeGzip,
        }, progress, log, ct).ConfigureAwait(false);

        if (!result.Ok)
            throw new InvalidOperationException($"Proton-GE download failed: {result.Error}");

        log?.Invoke("Extracting…");
        var extracted = await ExtractAsync(tarPath, CompatToolsDir, log, ct).ConfigureAwait(false);
        try { File.Delete(tarPath); } catch { }

        // The extracted top-level folder name comes from the archive, not the
        // asset name — trust the archive when it differs.
        var dir = Directory.Exists(finalDir) ? finalDir : extracted;
        var script = Path.Combine(dir, "proton");
        if (!File.Exists(script))
            throw new InvalidOperationException(
                $"Proton-GE extracted to {dir} but no 'proton' script is there.");

        log?.Invoke($"Installed to {dir} ({extracted} entries)");
        return dir;
    }

    /// <summary>Rejects a body that is not a gzip stream (CDN/error HTML).</summary>
    public static string? ProbeGzip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var magic = new byte[2];
            if (fs.Read(magic, 0, 2) < 2 || magic[0] != 0x1F || magic[1] != 0x8B)
                return $"{Path.GetFileName(path)} is not a gzip archive (first bytes: " +
                       $"{Convert.ToHexString(magic)}).";
            return null;
        }
        catch (Exception ex)
        {
            return "Cannot inspect " + Path.GetFileName(path) + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Extracts a .tar.gz under <paramref name="root"/>, refusing entries that
    /// escape it, and restores the unix mode of each entry so the "proton"
    /// wrapper stays executable. Returns the top-level directory created.
    /// </summary>
    public static async Task<string> ExtractAsync(
        string tarPath, string root, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var tops = new HashSet<string>(StringComparer.Ordinal);
        var modes = new List<(string Path, UnixFileMode Mode)>();

        await using (var fs = File.OpenRead(tarPath))
        await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            var reader = new TarReader(gz);
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false))
                   is not null)
            {
                ct.ThrowIfCancellationRequested();

                var name = entry.Name.Replace('\\', '/').TrimStart('/');
                if (name.Length == 0)
                    continue;

                var dest = Path.GetFullPath(Path.Combine(root, name));
                if (!dest.StartsWith(rootFull, StringComparison.Ordinal)
                    && !string.Equals(dest + Path.DirectorySeparatorChar, rootFull, StringComparison.Ordinal))
                {
                    log?.Invoke($"Skipped archive entry outside the target directory: {entry.Name}");
                    continue;
                }

                var first = name.Split('/', 2)[0];
                if (first.Length > 0)
                    tops.Add(first);

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(dest);
                        break;

                    case TarEntryType.SymbolicLink:
                    {
                        // Only link within the tree — an absolute or climbing
                        // target is how a tarball writes outside its own folder.
                        var target = entry.LinkName;
                        if (string.IsNullOrWhiteSpace(target)
                            || Path.IsPathRooted(target)
                            || target.Split('/').Contains(".."))
                        {
                            log?.Invoke($"Skipped suspicious symlink: {entry.Name} → {target}");
                            break;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        TryDeletePath(dest);
                        try { File.CreateSymbolicLink(dest, target); }
                        catch (Exception ex) { log?.Invoke($"Symlink {entry.Name} skipped: {ex.Message}"); }
                        break;
                    }

                    case TarEntryType.HardLink:
                        // Hard links are rare in these builds and their targets can
                        // be order-dependent; skip rather than half-create them.
                        break;

                    default:
                    {
                        if (entry.DataStream is null && entry.Length == 0)
                            break;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await entry.ExtractToFileAsync(dest, overwrite: true, ct).ConfigureAwait(false);
                        if (!OperatingSystem.IsWindows() && entry.Mode != UnixFileMode.None)
                            modes.Add((dest, entry.Mode));
                        break;
                    }
                }
            }
        }

        // TarEntry.ExtractToFileAsync creates files with default permissions, so
        // the executable bits the archive records are applied explicitly here.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var (path, mode) in modes)
            {
                try { File.SetUnixFileMode(path, mode); }
                catch { /* best effort: a missing exec bit only breaks the scripts */ }
            }
        }

        var top = tops.Count == 1 ? tops.First() : "";
        return top.Length > 0 ? Path.Combine(root, top) : root;
    }

    static void TryDeletePath(string path)
    {
        try
        {
            if (File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
