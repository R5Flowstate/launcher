using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Removes what the archive-based installer left behind once a track has been
/// installed from the content store.
///
/// The volume cache alone can hold tens of GB of 7z parts that will never be
/// read again, and a player who came from the old launcher has no way to know
/// they are there.
/// </summary>
public static class LegacyCleanup
{
    public sealed class Result
    {
        public int FilesRemoved { get; set; }
        public long BytesReclaimed { get; set; }
        public List<string> Removed { get; } = new();
    }

    /// <summary>
    /// Safe to call whenever a content-mode track has converged. Everything it
    /// deletes is either a download scratch file or an archive part, never game
    /// content: the reconciler owns the install tree and would have restored
    /// anything it still needed on the pass that just succeeded.
    /// </summary>
    public static Result Run(string installPath, IProgress<ContentInstallProgress>? progress = null)
    {
        var result = new Result();
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return result;

        var cacheRoot = Path.Combine(installPath, ProductConstants.ContentCacheDirName, "cache");
        RemoveTree(cacheRoot, result);

        // Volumes the old installer fetched next to the install root, and part
        // files from an interrupted transfer in either format.
        SweepStrays(installPath, result);

        if (result.FilesRemoved > 0)
        {
            progress?.Report(new ContentInstallProgress
            {
                Phase = "cleanup",
                Message =
                    $"removed {result.FilesRemoved} leftover files " +
                    $"({result.BytesReclaimed / 1_000_000_000.0:F1} GB reclaimed)",
            });
        }

        return result;
    }

    static void RemoveTree(string dir, Result result)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                Count(f, result);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked leftover is not worth failing an install over; the next
            // successful pass tries again.
        }
    }

    static void SweepStrays(string installPath, Result result)
    {
        // Directory patterns understand * and ? only; a [0-9] class silently
        // matches nothing, which is how this quietly swept up no archive parts.
        var patterns = new[] { "*.7z.*", "*.zip.*", "*.partial", "*" + ContentExecutor.PartSuffix };
        var roots = new List<string> { installPath };
        var r5f = Path.Combine(installPath, ProductConstants.ContentCacheDirName);
        if (Directory.Exists(r5f))
            roots.Add(r5f);
        foreach (var extra in new[] { "paks", "vpk" })
        {
            var dir = Path.Combine(installPath, extra);
            if (Directory.Exists(dir))
                roots.Add(dir);
        }

        foreach (var root in roots)
        {
            var recursive = !string.Equals(root, installPath, StringComparison.OrdinalIgnoreCase);
            foreach (var pattern in patterns)
            {
                IEnumerable<string> hits;
                try
                {
                    hits = Directory.EnumerateFiles(
                        root, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                IEnumerator<string> en;
                try
                {
                    en = hits.GetEnumerator();
                }
                catch
                {
                    continue;
                }

                while (true)
                {
                    string f;
                    try
                    {
                        if (!en.MoveNext())
                            break;
                        f = en.Current;
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromHours(6))
                            continue;
                        Count(f, result);
                        File.Delete(f);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        result.FilesRemoved--;
                    }
                }
            }
        }
    }

    static void Count(string path, Result result)
    {
        try
        {
            result.BytesReclaimed += new FileInfo(path).Length;
        }
        catch
        {
            // Size is only for the message.
        }
        result.FilesRemoved++;
        if (result.Removed.Count < 8)
            result.Removed.Add(path);
    }
}
