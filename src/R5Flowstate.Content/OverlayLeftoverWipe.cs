using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// After a platform InstallBase: delete overlay-owned dest files absent from the new SHARE.
/// Never deletes fat-owned or omitted overlay-optional paths.
/// </summary>
public static class OverlayLeftoverWipe
{
    public const string LastShareRelative = ".r5f/platform/last_share.json";

    public static int Apply(string installPath, ShareManifest newShare)
    {
        ArgumentNullException.ThrowIfNull(newShare);
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ShareFileVerifier.EnumerateFiles(newShare))
        {
            var n = OverlayPaths.Norm(f.Path);
            if (n.Length > 0)
                keep.Add(n);
        }

        var deleted = 0;
        foreach (var prefix in new[]
                 {
                     "platform/scripts",
                     "platform/cfg",
                     "platform/localization",
                     "platform/datatable",
                     "platform/resource",
                 })
        {
            deleted += WipeUnder(installPath, prefix, keep);
        }

        foreach (var exact in new[]
                 {
                     "platform/playlists_r5_patch.txt",
                     "platform/r5f_map_names.txt",
                     "playlists_r5_patch.txt",
                     "r2/playlists_r5.txt",
                 })
        {
            if (keep.Contains(exact))
                continue;
            var full = Path.Combine(installPath, exact.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                File.Delete(full);
                deleted++;
            }
        }

        return deleted;
    }

    public static string LastSharePath(string installPath) =>
        Path.Combine(installPath, LastShareRelative.Replace('/', Path.DirectorySeparatorChar));

    static int WipeUnder(string installPath, string destPrefix, HashSet<string> keep)
    {
        var dir = Path.Combine(installPath, destPrefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir))
            return 0;

        var n = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = OverlayPaths.Norm(Path.GetRelativePath(installPath, file));
            if (!OverlayPaths.IsOverlayOwned(rel))
                continue;
            if (keep.Contains(rel))
                continue;
            File.Delete(file);
            n++;
        }

        return n;
    }
}
