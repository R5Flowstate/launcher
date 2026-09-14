using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Idle re-hash of one official fat file. Catches a same-size, mtime-preserved
/// rewrite that Overlay depth cannot see.
/// </summary>
public static class ContentProofSweep
{
    public sealed class Result
    {
        public bool Ran { get; init; }
        public bool Mismatch { get; init; }
        public string? Path { get; init; }
        public int Cursor { get; init; }
    }

    public static Result Step(
        string installPath,
        ContentManifest manifest,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(installPath))
            return new Result();

        var indexPath = Path.Combine(installPath, ProductConstants.InstallFilesFileName);
        var index = InstallFilesIndexIO.TryLoad(indexPath);
        if (index is null || index.Files.Count == 0)
            return new Result();

        var serial = InstallFilesIndexIO.ReadVolumeSerial(installPath);
        if (!index.DescribesSameTarget(installPath, serial))
            return new Result();

        var fat = new List<ContentFile>();
        foreach (var f in manifest.Files)
        {
            if (OverlayPaths.IsOverlayOwned(f.Path) || OverlayPaths.IsOverlayOptional(f.Path))
                continue;
            if (OverlayPaths.IsOptOwned(f.Path))
                continue;
            fat.Add(f);
        }
        fat.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        if (fat.Count == 0)
            return new Result();

        var cursor = index.SweepCursor % fat.Count;
        if (cursor < 0)
            cursor = 0;
        var entry = fat[cursor];
        var next = (cursor + 1) % fat.Count;

        var norm = OverlayPaths.Norm(entry.Path);
        if (!SafePath.TryJoin(installPath, entry.Path, out var full) || !File.Exists(full))
        {
            index.ProofMismatch = entry.Path;
            if (index.Files.TryGetValue(norm, out var missing))
            {
                missing.Sha256 = null;
                missing.VerifiedUtcTicks = 0;
            }
            index.SweepCursor = next;
            InstallFilesIndexIO.Save(indexPath, index);
            return new Result { Ran = true, Mismatch = true, Path = entry.Path, Cursor = next };
        }

        cancel.ThrowIfCancellationRequested();
        var actual = ContentReconciler.HashFile(full, cancel);
        var miss = !string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase);

        var info = new FileInfo(full);
        if (!miss)
        {
            index.Files[norm] = new InstallFileState
            {
                Size = info.Length,
                MTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = entry.Sha256,
                VerifiedUtcTicks = DateTime.UtcNow.Ticks,
            };
            if (string.Equals(index.ProofMismatch, entry.Path, StringComparison.OrdinalIgnoreCase))
                index.ProofMismatch = null;
        }
        else
        {
            index.ProofMismatch = entry.Path;
            if (index.Files.TryGetValue(norm, out var known))
            {
                known.Sha256 = null;
                known.VerifiedUtcTicks = 0;
            }
        }
        index.SweepCursor = next;
        InstallFilesIndexIO.Save(indexPath, index);

        return new Result
        {
            Ran = true,
            Mismatch = miss,
            Path = entry.Path,
            Cursor = next,
        };
    }
}
