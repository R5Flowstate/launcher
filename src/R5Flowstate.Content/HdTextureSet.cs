using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// HD texture opt-out. The client decides HD is present by probing four core
/// .opt.starpak on disk, so leaving the files behind after the player turns HD
/// off means they keep getting HD -- the toggle has to delete, not just record.
/// </summary>
public static class HdTextureSet
{
    /// <summary>Bytes an HD install adds, for the free-space gate. Payload only.</summary>
    public static long RequiredBytes(ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        long n = 0;
        foreach (var f in manifest.Files)
        {
            if (OverlayPaths.IsOptOwned(f.Path))
                n += f.Size;
        }
        return n;
    }

    /// <summary>
    /// Bytes still to fetch: an HD file already on disk at the right size is not
    /// charged again, so a resumed or partial install does not over-quote.
    /// </summary>
    public static long MissingBytes(string installPath, ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        long n = 0;
        foreach (var f in manifest.Files)
        {
            if (!OverlayPaths.IsOptOwned(f.Path))
                continue;
            if (!SafePath.TryJoin(installPath, f.Path, out var full))
                continue;
            var info = new FileInfo(full);
            if (!info.Exists || info.Length != f.Size)
                n += f.Size;
        }
        return n;
    }

    /// <summary>
    /// Delete every HD texture under the install. Returns (deleted, bytes).
    /// A file that will not delete is reported, not thrown: a locked pak must
    /// not strand the player with HD half on.
    /// </summary>
    public static (int Deleted, long Bytes, List<string> Failed) RemoveAll(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var deleted = 0;
        long bytes = 0;
        var failed = new List<string>();

        var paks = Path.Combine(installPath, "paks");
        if (!Directory.Exists(paks))
            return (0, 0, failed);

        foreach (var file in Directory.EnumerateFiles(paks, "*", SearchOption.AllDirectories))
        {
            var rel = OverlayPaths.Norm(Path.GetRelativePath(installPath, file));
            if (!OverlayPaths.IsOptOwned(rel))
                continue;
            try
            {
                var len = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                bytes += len;
            }
            catch (Exception)
            {
                failed.Add(rel);
            }
        }

        return (deleted, bytes, failed);
    }

    /// <summary>True while any HD texture is still on disk.</summary>
    public static bool AnyPresent(string installPath)
    {
        var paks = Path.Combine(installPath, "paks");
        if (!Directory.Exists(paks))
            return false;
        foreach (var file in Directory.EnumerateFiles(paks, "*.opt.starpak", SearchOption.AllDirectories))
            return true;
        return false;
    }
}
