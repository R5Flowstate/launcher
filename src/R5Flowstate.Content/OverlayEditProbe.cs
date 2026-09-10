using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Compare overlay-owned trees on disk to what the install should contain.
/// Extra or changed files are player edits; missing official files are not.
///
/// When INSTALL_FILES.json is present the comparison is by sha256. A script edit
/// very often leaves the file the same length, which a size comparison cannot
/// see at all -- so size is only the fallback for an install that predates the
/// per-file index.
/// </summary>
public static class OverlayEditProbe
{
    public static OverlayEditReport Scan(string installPath, ChannelManifest? channel)
    {
        var report = new OverlayEditReport();
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return report;

        var index = InstallFilesIndexIO.TryLoad(
            Path.Combine(installPath, ProductConstants.InstallFilesFileName));
        if (index is not null && index.Files.Count > 0)
            return ScanByHash(installPath, index, report);

        var official = OfficialOverlaySizes(installPath, channel);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in OverlayPaths.OverlayOwnedPrefixes)
        {
            var dir = Path.Combine(installPath, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = OverlayPaths.Norm(Path.GetRelativePath(installPath, file));
                if (!OverlayPaths.IsOverlayOwned(rel))
                    continue;
                seen.Add(rel);
                if (!official.TryGetValue(rel, out var want))
                {
                    report.Extra.Add(rel);
                    continue;
                }
                try
                {
                    if (want > 0 && new FileInfo(file).Length != want)
                        report.Changed.Add(rel);
                }
                catch
                {
                    report.Changed.Add(rel);
                }
            }
        }

        foreach (var exact in OverlayPaths.OverlayOwnedExact)
        {
            var full = Path.Combine(installPath, exact.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                continue;
            if (!official.TryGetValue(exact, out var want))
            {
                report.Extra.Add(exact);
                continue;
            }
            try
            {
                if (want > 0 && new FileInfo(full).Length != want)
                    report.Changed.Add(exact);
            }
            catch
            {
                report.Changed.Add(exact);
            }
        }

        return report;
    }

    /// <summary>
    /// Hash every overlay-owned file the index knows about. The overlay tree is
    /// a couple of thousand small files, so this is cheap enough to do on every
    /// launch -- and it is the only way a same-size edit is ever noticed.
    /// </summary>
    static OverlayEditReport ScanByHash(
        string installPath, InstallFilesIndex index, OverlayEditReport report)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in index.Files)
        {
            var rel = OverlayPaths.Norm(kv.Key);
            if (!OverlayPaths.IsOverlayOwned(rel))
                continue;
            known.Add(rel);

            if (!SafePath.TryJoin(installPath, rel, out var full) || !File.Exists(full))
                continue;

            var state = kv.Value;
            if (state.PlayerEdited)
            {
                report.Changed.Add(rel);
                continue;
            }
            if (string.IsNullOrEmpty(state.Sha256))
                continue;

            try
            {
                var info = new FileInfo(full);
                if (state.StatMatches(info.Length, info.LastWriteTimeUtc.Ticks))
                    continue;
                if (!string.Equals(
                        ContentReconciler.HashFile(full), state.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    report.Changed.Add(rel);
                }
            }
            catch
            {
                report.Changed.Add(rel);
            }
        }

        foreach (var prefix in OverlayPaths.OverlayOwnedPrefixes)
        {
            var dir = Path.Combine(
                installPath, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = OverlayPaths.Norm(Path.GetRelativePath(installPath, file));
                if (OverlayPaths.IsOverlayOwned(rel) && !known.Contains(rel))
                    report.Extra.Add(rel);
            }
        }

        return report;
    }

    public static Dictionary<string, long> OfficialOverlaySizes(
        string installPath,
        ChannelManifest? channel)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var manPath in CandidateManifests(installPath, channel))
        {
            try
            {
                var man = ShareManifestIO.Load(manPath);
                var n = 0;
                foreach (var f in ShareFileVerifier.EnumerateFiles(man))
                {
                    if (!OverlayPaths.IsOverlayOwned(f.Path))
                        continue;
                    var rel = OverlayPaths.Norm(f.Path);
                    if (rel.Length == 0)
                        continue;
                    sizes[rel] = f.Size;
                    n++;
                }
                if (n > 0)
                    return sizes;
            }
            catch
            {
                // try the next cache
            }
            sizes.Clear();
        }

        return sizes;
    }

    static IEnumerable<string> CandidateManifests(string installPath, ChannelManifest? channel)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Offer(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            if (seen.Add(path))
                list.Add(path);
        }

        var baseVer = channel?.Client?.Base?.CatalogVersion;
        var tipVer = channel?.Client?.CatalogVersion;
        Offer(InstallHealthAssessor.FindCachedShareManifest(installPath, "client", baseVer));
        if (!string.Equals(tipVer, baseVer, StringComparison.Ordinal))
            Offer(InstallHealthAssessor.FindCachedShareManifest(installPath, "client", tipVer));
        Offer(InstallHealthAssessor.FindCachedShareManifest(installPath, "client", null));
        Offer(InstallHealthAssessor.FindCachedShareManifest(installPath, "platform", null));
        return list;
    }
}
