using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public sealed class TrackInstallResult
{
    public string Preset { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string CatalogVersion { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int VolumeRegets { get; set; }
    public int VerifyPasses { get; set; }

    /// <summary>Files hashed on the last reconcile pass. Settlement should be near zero.</summary>
    public int LastScanHashed { get; set; }
}

public static class TrackInstaller
{
    /// <summary>Max re-download attempts per volume after sha256 failure.</summary>
    public const int MaxVolumeRegets = 2;

    /// <summary>Full re-download+unpack cycles if post-unpack file verify fails.</summary>
    public const int MaxTrackRepairPasses = 2;

    public static async Task<TrackInstallResult> InstallTrackAsync(
        TrackPlan track,
        string installPath,
        string cacheRoot,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        OverlayExtractPolicy overlay = OverlayExtractPolicy.WriteOfficial)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(fetcher);
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new ArgumentException("Cache root is required.", nameof(cacheRoot));

        var result = new TrackInstallResult
        {
            Preset = track.Preset,
            ContentHash = track.ContentHash,
            CatalogVersion = track.CatalogVersion,
        };

        try
        {
            var trackCache = Path.Combine(
                cacheRoot,
                SanitizeDirSegment($"{track.Preset}_{track.CatalogVersion}"));
            Directory.CreateDirectory(trackCache);

            for (var pass = 1; pass <= MaxTrackRepairPasses; pass++)
            {
                cancel.ThrowIfCancellationRequested();
                result.VerifyPasses = pass;

                progress?.Report(new ContentInstallProgress
                {
                    Phase = "manifest",
                    Track = track.Preset,
                    Message = pass == 1
                        ? "download SHARE_MANIFEST"
                        : $"repair pass {pass}: re-download SHARE_MANIFEST",
                });

                if (string.IsNullOrWhiteSpace(track.ShareManifestUrl))
                    throw new InvalidOperationException(
                        $"Track '{track.Preset}' has no share_manifest_url / share_manifest asset.");

                var manDest = Path.Combine(trackCache, ProductConstants.ShareManifestFileName);
                var manSha = FindAssetSha(track, "share_manifest", ProductConstants.ShareManifestFileName);

                // Repair pass: force re-fetch manifest if present.
                if (pass > 1 && File.Exists(manDest))
                    TryDelete(manDest);

                await fetcher.DownloadAsync(
                    track.ShareManifestUrl,
                    manDest,
                    manSha,
                    WrapProgress(progress, track.Preset, "manifest"),
                    cancel).ConfigureAwait(false);

                var manifest = ShareManifestIO.Load(manDest);
                var assetMap = BuildVolumeAssetMap(track);
                var volumeNames = CollectVolumeNames(manifest);

                await DownloadVolumesWithRegetAsync(
                    track,
                    trackCache,
                    volumeNames,
                    assetMap,
                    manifest,
                    fetcher,
                    progress,
                    result,
                    forceRedownload: pass > 1,
                    cancel).ConfigureAwait(false);

                progress?.Report(new ContentInstallProgress
                {
                    Phase = "unpack",
                    Track = track.Preset,
                    Message = $"unpack {track.Preset} -> {installPath}",
                });

                ShareUnpacker.Unpack(
                    trackCache,
                    installPath,
                    password: null,
                    progress: WrapProgress(progress, track.Preset, "unpack"),
                    cancel: cancel,
                    overlay: overlay);

                // Post-unpack file verify; reget whole track if broken.
                Func<string, bool>? skip = IsFatPreset(track.Preset)
                    ? OverlayPaths.SkipFatVerify
                    : null;
                var vr = ShareFileVerifier.VerifyInstallFiles(installPath, manifest, skipRelativePath: skip);
                if (!vr.Ok)
                {
                    progress?.Report(new ContentInstallProgress
                    {
                        Phase = "verify",
                        Track = track.Preset,
                        Message =
                            $"post-unpack broken missing={vr.Missing} size={vr.SizeMismatch} " +
                            $"(pass {pass}/{MaxTrackRepairPasses})",
                    });

                    if (pass >= MaxTrackRepairPasses)
                    {
                        throw new InvalidOperationException(
                            $"Track '{track.Preset}' files still broken after {pass} passes: " +
                            string.Join("; ", vr.Samples.Take(4)));
                    }

                    // Drop volume cache so next pass re-downloads clean parts.
                    foreach (var name in volumeNames)
                        TryDelete(Path.Combine(trackCache, name));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(manifest.ContentHash))
                    result.ContentHash = manifest.ContentHash;
                if (!string.IsNullOrWhiteSpace(manifest.Version) &&
                    string.IsNullOrWhiteSpace(result.CatalogVersion))
                    result.CatalogVersion = manifest.Version;

                result.Success = true;
                foreach (var name in volumeNames)
                    TryDelete(Path.Combine(trackCache, name));
                progress?.Report(new ContentInstallProgress
                {
                    Phase = "done",
                    Track = track.Preset,
                    Current = 1,
                    Total = 1,
                    Message =
                        $"track {track.Preset} ok (regets={result.VolumeRegets} passes={pass})",
                });
                return result;
            }

            throw new InvalidOperationException(
                $"Track '{track.Preset}' failed all repair passes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    static async Task DownloadVolumesWithRegetAsync(
        TrackPlan track,
        string trackCache,
        List<string> volumeNames,
        Dictionary<string, ChannelAssetRef> assetMap,
        ShareManifest manifest,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress,
        TrackInstallResult result,
        bool forceRedownload,
        CancellationToken cancel)
    {
        var total = volumeNames.Count;
        var sizes = new long[total];
        long jobTotal = 0;
        for (var n = 0; n < total; n++)
        {
            sizes[n] = Math.Max(0, ExpectedVolumeSize(manifest, assetMap, volumeNames[n]));
            jobTotal += sizes[n];
        }

        long jobDone = 0;
        for (var i = 0; i < volumeNames.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var name = volumeNames[i];
            if (!IsSafeVolumeFileName(name, out var nameErr))
            {
                throw new InvalidOperationException(
                    $"Refusing unsafe volume name '{name}': {nameErr}");
            }
            var cacheFull = Path.GetFullPath(trackCache);
            var dest = Path.GetFullPath(Path.Combine(trackCache, name));
            var cachePrefix = cacheFull.EndsWith(Path.DirectorySeparatorChar)
                ? cacheFull
                : cacheFull + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing volume path outside cache: {name}");
            }
            ResolveVolumeSource(track, assetMap, manifest, name, out var url, out var sha);

            if (forceRedownload && File.Exists(dest))
                TryDelete(dest);

            var want = sizes[i];
            if (!forceRedownload && VolumeAlreadyOnDisk(dest, want))
            {
                jobDone += want;
                progress?.Report(new ContentInstallProgress
                {
                    Phase = "download",
                    Track = track.Preset,
                    FileName = name,
                    Message = $"skip (on disk): {name}",
                    StepIndex = i + 1,
                    StepCount = total,
                    Current = want,
                    Total = want,
                    JobCurrent = jobDone,
                    JobTotal = jobTotal,
                });
                continue;
            }

            progress?.Report(new ContentInstallProgress
            {
                Phase = "download",
                Track = track.Preset,
                FileName = name,
                Message = name,
                StepIndex = i + 1,
                StepCount = total,
                JobCurrent = jobDone,
                JobTotal = jobTotal,
            });

            var stamped = StampProgress(
                progress, track.Preset, "download", i + 1, total, name, jobDone, jobTotal);
            await DownloadOneVolumeWithRegetAsync(
                url, dest, sha, track.Preset, name, fetcher, stamped, result, cancel)
                .ConfigureAwait(false);
            jobDone += want;
        }
    }

    static async Task DownloadOneVolumeWithRegetAsync(
        string url,
        string dest,
        string? sha,
        string preset,
        string name,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress,
        TrackInstallResult result,
        CancellationToken cancel)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= MaxVolumeRegets; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                {
                    result.VolumeRegets++;
                    TryDelete(dest);
                    progress?.Report(new ContentInstallProgress
                    {
                        Phase = "reget",
                        Track = preset,
                        Message = $"reget {name} attempt {attempt}/{MaxVolumeRegets}",
                    });
                }

                await fetcher.DownloadAsync(
                    url,
                    dest,
                    sha,
                    WrapProgress(progress, preset, "download"),
                    cancel).ConfigureAwait(false);

                if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                    throw new InvalidOperationException($"Empty volume after download: {name}");

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                TryDelete(dest);
            }
        }

        throw new InvalidOperationException(
            $"Failed to download volume '{name}' after regets: {last?.Message ?? "unknown"}");
    }

    static long ExpectedVolumeSize(
        ShareManifest manifest,
        Dictionary<string, ChannelAssetRef> assetMap,
        string name)
    {
        if (manifest.Volumes is { Count: > 0 })
        {
            foreach (var vol in manifest.Volumes)
            {
                if (string.Equals(vol.Name, name, StringComparison.OrdinalIgnoreCase))
                    return vol.EffectiveSize;
            }
        }

        if (assetMap.TryGetValue(name, out var asset) && asset.Size is > 0)
            return asset.Size.Value;
        return 0;
    }

    static bool VolumeAlreadyOnDisk(string dest, long expectedSize)
    {
        try
        {
            if (!File.Exists(dest))
                return false;
            var n = new FileInfo(dest).Length;
            if (n <= 0)
                return false;
            return expectedSize <= 0 || n == expectedSize;
        }
        catch
        {
            return false;
        }
    }

    static void ResolveVolumeSource(
        TrackPlan track,
        Dictionary<string, ChannelAssetRef> assetMap,
        ShareManifest manifest,
        string name,
        out string url,
        out string? sha)
    {
        sha = null;
        if (assetMap.TryGetValue(name, out var asset))
        {
            url = asset.Url;
            sha = asset.Sha256;
        }
        else
        {
            url = ResolveSiblingUrl(track.ShareManifestUrl, name);
            sha = FindVolumeSha(manifest, name);
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"Cannot resolve URL for volume '{name}' on track '{track.Preset}'.");
    }

    static Dictionary<string, ChannelAssetRef> BuildVolumeAssetMap(TrackPlan track)
    {
        var map = new Dictionary<string, ChannelAssetRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in track.Assets)
        {
            if (string.IsNullOrWhiteSpace(a.Name))
                continue;
            if (string.Equals(a.Kind, "share_volume", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Kind, "7z_volume", StringComparison.OrdinalIgnoreCase))
            {
                map[a.Name] = a;
            }
        }
        return map;
    }

    static List<string> CollectVolumeNames(ShareManifest man)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? n)
        {
            if (string.IsNullOrWhiteSpace(n))
                return;
            if (seen.Add(n))
                names.Add(n);
        }

        if (man.Volumes is { Count: > 0 })
        {
            foreach (var vol in man.Volumes)
                Add(vol.Name);
            return names;
        }

        if (man.Archives is { Count: > 0 })
        {
            foreach (var arch in man.Archives)
            {
                if (arch.Volumes is { Count: > 0 })
                {
                    foreach (var v in arch.Volumes)
                        Add(v);
                }
                else if (!string.IsNullOrEmpty(arch.ArchiveBase))
                {
                    Add(arch.ArchiveBase + ".001");
                }
            }
        }

        return names;
    }

    static string? FindAssetSha(TrackPlan track, string kind, string name)
    {
        foreach (var a in track.Assets)
        {
            if (string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(name) ||
                 string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return a.Sha256;
            }
        }
        return null;
    }

    static string? FindVolumeSha(ShareManifest man, string name)
    {
        foreach (var vol in man.Volumes)
        {
            if (string.Equals(vol.Name, name, StringComparison.OrdinalIgnoreCase))
                return vol.Sha256;
        }
        return null;
    }

    static string ResolveSiblingUrl(string shareManifestUrl, string volumeName)
    {
        if (string.IsNullOrWhiteSpace(shareManifestUrl))
            return volumeName;

        if (Path.IsPathRooted(shareManifestUrl) && !shareManifestUrl.Contains("://", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(shareManifestUrl);
            return dir is null ? volumeName : Path.Combine(dir, volumeName);
        }

        if (!Uri.TryCreate(shareManifestUrl, UriKind.Absolute, out var uri))
            return volumeName;

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            var local = uri.LocalPath;
            if (OperatingSystem.IsWindows() &&
                local.Length >= 3 &&
                local[0] == '/' &&
                char.IsLetter(local[1]) &&
                local[2] == ':')
            {
                local = local[1..];
            }
            var dir = Path.GetDirectoryName(local);
            return dir is null ? volumeName : Path.Combine(dir, volumeName);
        }

        var baseUri = new Uri(uri, ".");
        return new Uri(baseUri, volumeName).ToString();
    }

    static string SanitizeDirSegment(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "track" : s;
    }

    static bool IsFatPreset(string preset) =>
        string.Equals(preset, "client", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset, "server", StringComparison.OrdinalIgnoreCase);

    static bool IsSafeVolumeFileName(string name, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "empty name";
            return false;
        }
        if (Path.IsPathRooted(name) || name.Contains("..", StringComparison.Ordinal))
        {
            error = "path traversal";
            return false;
        }
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            error = "not a plain file name";
            return false;
        }
        return true;
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }

    static IProgress<ContentInstallProgress>? WrapProgress(
        IProgress<ContentInstallProgress>? inner,
        string track,
        string defaultPhase) =>
        StampProgress(inner, track, defaultPhase, stepIndex: 0, stepCount: 0, fileName: null);

    static IProgress<ContentInstallProgress>? StampProgress(
        IProgress<ContentInstallProgress>? inner,
        string track,
        string defaultPhase,
        int stepIndex,
        int stepCount,
        string? fileName,
        long jobBase = 0,
        long jobTotal = 0)
    {
        if (inner is null)
            return null;
        return new Progress<ContentInstallProgress>(p =>
        {
            if (string.IsNullOrEmpty(p.Track))
                p.Track = track;
            if (string.IsNullOrEmpty(p.Phase))
                p.Phase = defaultPhase;
            if (string.IsNullOrEmpty(p.FileName) && !string.IsNullOrEmpty(fileName))
                p.FileName = fileName;
            if (p.StepCount <= 0 && stepCount > 0)
            {
                p.StepIndex = stepIndex;
                p.StepCount = stepCount;
            }
            if (jobTotal > 0 && p.JobTotal <= 0)
            {
                p.JobTotal = jobTotal;
                p.JobCurrent = jobBase + Math.Max(0, p.Current);
            }
            inner.Report(p);
        });
    }
}
