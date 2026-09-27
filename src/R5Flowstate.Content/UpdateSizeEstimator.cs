using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// What an update to a content-manifest tip will download, from the files
/// index rather than a hash scan. The CHANNEL only carries the size of the
/// whole track, which on an update is off by tens of GB.
/// </summary>
public static class UpdateSizeEstimator
{
    /// <summary>Null when there is no usable index for this install (fresh or moved folder).</summary>
    public static async Task<long?> EstimateAsync(
        ChannelManifest channel,
        ChannelTrackTip tip,
        string preset,
        string installPath,
        IHttpFetcher fetcher,
        CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(tip);
        if (!tip.UsesContentManifest || string.IsNullOrWhiteSpace(installPath))
            return null;

        var index = InstallFilesIndexIO.TryLoad(
            Path.Combine(installPath, ProductConstants.InstallFilesFileName));
        if (index is null || index.Files.Count == 0 ||
            !index.DescribesSameTarget(installPath, InstallFilesIndexIO.ReadVolumeSerial(installPath)))
            return null;

        var step = UpdatePlanner.Resolve(tip, null, null, channel.BaseUrl).Steps
            .FirstOrDefault(s => s.Kind == UpdateStepKind.SyncFiles);
        if (step is null)
            return null;

        // Same cache path the installer reads, so the install reuses this download.
        var manifest = await ContentTrackInstaller.LoadManifestAsync(
            step, installPath, preset, fetcher, null, cancel).ConfigureAwait(false);
        return Estimate(manifest, index, installPath);
    }

    /// <summary>
    /// Whole-file upper bound per distinct object: a chunked file may fetch
    /// less, never more.
    /// </summary>
    public static long Estimate(ContentManifest tip, InstallFilesIndex index, string installPath)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(index);

        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var f in tip.Files)
        {
            if (OverlayPaths.IsPlayerOwned(f.Path) || IsCurrent(f, index, installPath))
                continue;
            if (counted.Add(f.Sha256))
                bytes += f.Size;
        }
        return bytes;
    }

    static bool IsCurrent(ContentFile f, InstallFilesIndex index, string installPath)
    {
        if (!index.Files.TryGetValue(OverlayPaths.Norm(f.Path), out var known))
            return false;
        if (!known.PlayerEdited &&
            !string.Equals(known.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!SafePath.TryJoin(installPath, f.Path, out var full))
            return false;

        var info = new FileInfo(full);
        return info.Exists && known.StatMatches(info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
