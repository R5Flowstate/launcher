using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>Presence + size checks for SHARE_MANIFEST file lists under InstallPath.</summary>
public static class ShareFileVerifier
{
    public const int DefaultMaxSamples = 12;

    public sealed class Result
    {
        public int Checked { get; set; }
        public int Missing { get; set; }
        public int SizeMismatch { get; set; }
        public List<string> Samples { get; set; } = new();
        public bool Ok => Missing == 0 && SizeMismatch == 0;
    }

    public static Result VerifyInstallFiles(
        string installPath,
        ShareManifest manifest,
        int maxSamples = DefaultMaxSamples,
        Func<string, bool>? skipRelativePath = null,
        IProgress<(int current, int total, string path)>? progress = null,
        Func<string, bool>? sizeOptionalRelativePath = null)
    {
        var result = new Result();
        var files = new List<ShareFileEntry>();
        foreach (var file in EnumerateFiles(manifest))
        {
            if (skipRelativePath is not null && skipRelativePath(file.Path))
                continue;
            if (!string.IsNullOrWhiteSpace(file.Path))
                files.Add(file);
        }

        var total = files.Count;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            result.Checked++;
            var rel = file.Path.Replace('/', Path.DirectorySeparatorChar);
            if (progress is not null &&
                (i == 0 || i + 1 == total || ((i + 1) % 64) == 0))
            {
                progress.Report((i + 1, total, rel));
            }

            if (string.IsNullOrWhiteSpace(rel) || rel.EndsWith(Path.DirectorySeparatorChar))
                continue;

            var full = Path.Combine(installPath, rel);
            if (!File.Exists(full))
            {
                result.Missing++;
                AddSample(result.Samples, maxSamples, "missing: " + rel);
                continue;
            }

            if (file.Size > 0 &&
                (sizeOptionalRelativePath is null || !sizeOptionalRelativePath(file.Path)))
            {
                var len = new FileInfo(full).Length;
                if (len != file.Size)
                {
                    result.SizeMismatch++;
                    AddSample(result.Samples, maxSamples,
                        $"size: {rel} have={len} want={file.Size}");
                }
            }
        }

        return result;
    }

    public static IEnumerable<ShareFileEntry> EnumerateFiles(ShareManifest manifest)
    {
        if (manifest.Archives is { Count: > 0 })
        {
            foreach (var arch in manifest.Archives)
            {
                if (arch.Files is null)
                    continue;
                foreach (var f in arch.Files)
                {
                    if (!string.IsNullOrWhiteSpace(f.Path))
                        yield return f;
                }
            }
            yield break;
        }

        // Fallback: path_index keys only (size unknown).
        if (manifest.PathIndex is { Count: > 0 })
        {
            foreach (var path in manifest.PathIndex.Keys)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    yield return new ShareFileEntry { Path = path, Size = 0 };
            }
        }
    }

    static void AddSample(List<string> samples, int max, string line)
    {
        if (samples.Count < max)
            samples.Add(line);
    }
}
