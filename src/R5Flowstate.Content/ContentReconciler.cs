using System.Security.Cryptography;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public enum FileActionKind
{
    /// <summary>Absent or wrong; fetch the whole file.</summary>
    Fetch = 0,

    /// <summary>Present and the right size; only some chunks are wrong.</summary>
    FetchChunks = 1,

    /// <summary>Matches the manifest.</summary>
    Keep = 2,

    /// <summary>Under a root the manifest owns but not in the manifest.</summary>
    Delete = 3,

    /// <summary>Differs, but it is an overlay path the player is allowed to edit.</summary>
    KeepPlayerEdit = 4,
}

/// <summary>How hard to look before believing the index.</summary>
public enum VerifyDepth
{
    /// <summary>Size and mtime only. Sub-second over ~10k files.</summary>
    Stat = 0,

    /// <summary>Stat everything, hash the overlay tree. The launch default.</summary>
    Overlay = 1,

    /// <summary>Hash everything the manifest lists.</summary>
    Full = 2,
}

public sealed record FileAction(
    string Path,
    ContentFile? Entry,
    FileActionKind Kind,
    IReadOnlyList<int>? Chunks = null,
    long Bytes = 0);

public sealed class ReconcilePlan
{
    public List<FileAction> Actions { get; } = new();
    public int Hashed { get; set; }
    public long HashedBytes { get; set; }

    /// <summary>
    /// Files confirmed against the manifest during this scan. Adopting an
    /// existing install means hashing tens of GB once; recording the result
    /// as it goes is what stops an interrupted adoption starting over.
    /// </summary>
    public Dictionary<string, InstallFileState> Verified { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<FileAction> Work =>
        Actions.Where(a => a.Kind is FileActionKind.Fetch or FileActionKind.FetchChunks);

    public IEnumerable<FileAction> Deletions =>
        Actions.Where(a => a.Kind == FileActionKind.Delete);

    public IEnumerable<FileAction> PlayerEdits =>
        Actions.Where(a => a.Kind == FileActionKind.KeepPlayerEdit);

    public long BytesToFetch => Work.Sum(a => a.Bytes);

    /// <summary>Nothing left to do. The loop in ContentReconciler runs until this is true.</summary>
    public bool IsConverged => !Work.Any() && !Deletions.Any();

    /// <summary>
    /// A plan proposing to delete a large share of what it manages is not
    /// describing this install. Two ways that happens: the folder is not a
    /// player install at all -- a build or master tree legitimately holds far
    /// more under the same roots -- or the published file list is truncated.
    /// Neither is worth finding out about after the files are gone.
    /// </summary>
    public bool DeletionsLookWrong(int manifestFileCount)
    {
        var deletions = Deletions.Count();
        if (deletions == 0)
            return false;
        var ceiling = Math.Max(500, manifestFileCount / 4);
        return deletions > ceiling;
    }

    public string DescribeDeletionRefusal(int manifestFileCount) =>
        $"Refusing to continue: this would delete {Deletions.Count()} files the manifest " +
        $"does not list, against {manifestFileCount} it does. That usually means the folder " +
        "is not a game install, or the published file list is incomplete.";
}

/// <summary>
/// Scan the tree, diff it against the manifest, and say what to do. Install,
/// update, repair and verify are all this function with a different depth --
/// which is why "the user deleted files mid-download", "the process was killed"
/// and "a chunk failed verification" need no separate handling anywhere.
/// </summary>
public static class ContentReconciler
{
    /// <summary>Hashed files recorded between checkpoints.</summary>
    public const int CheckpointEvery = 1500;

    public static ReconcilePlan Plan(
        ContentManifest manifest,
        InstallFilesIndex? index,
        string installPath,
        OverlayExtractPolicy overlay = OverlayExtractPolicy.KeepEdits,
        VerifyDepth depth = VerifyDepth.Overlay,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        Action<ReconcilePlan>? checkpoint = null,
        string track = "",
        bool trustSize = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var plan = new ReconcilePlan();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = manifest.Files.Count;

        long payload = 0;
        foreach (var f in manifest.Files)
            payload += f.Size;

        var reporter = new ScanReporter(progress, track, total, payload);

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = manifest.Files[i];

            if (!SafePath.TryJoin(installPath, entry.Path, out var full))
            {
                // The producer gate rejects these, so reaching one here means the
                // manifest was tampered with after signing-by-hash.
                throw new InvalidOperationException(
                    $"CONTENT_MANIFEST contains an unsafe path: {entry.Path}");
            }

            wanted.Add(OverlayPaths.Norm(entry.Path));

            reporter.Begin(entry.Path);
            plan.Actions.Add(Decide(entry, full, index, overlay, depth, plan, reporter, cancel, trustSize));
            reporter.Finish(entry.Size);

            if (checkpoint is not null && plan.Hashed > 0 &&
                plan.Hashed % CheckpointEvery == 0 && plan.Verified.Count > 0)
            {
                checkpoint(plan);
            }
        }

        reporter.Flush();
        reporter.Sweep();
        AddDeletions(manifest, installPath, wanted, overlay, index, plan, cancel);
        return plan;
    }

    /// <summary>
    /// Emits while the scan runs rather than once per batch of files. A single
    /// multi-GB hash is one loop iteration, so a per-file cadence leaves the UI
    /// motionless for minutes on exactly the files that take longest.
    /// </summary>
    sealed class ScanReporter
    {
        const int IntervalMs = 150;

        readonly IProgress<ContentInstallProgress>? _sink;
        readonly string _track;
        readonly int _files;
        readonly long _payload;
        DateTime _last = DateTime.MinValue;
        long _scanned;
        long _fileTicked;
        int _done;
        string _file = string.Empty;

        public ScanReporter(
            IProgress<ContentInstallProgress>? sink, string track, int files, long payload)
        {
            _sink = sink;
            _track = track;
            _files = files;
            _payload = payload;
        }

        public void Begin(string path)
        {
            _file = path;
            _fileTicked = 0;
            Emit(false, "scan");
        }

        public void Tick(long read)
        {
            _scanned += read;
            _fileTicked += read;
            Emit(false, "scan");
        }

        public void Finish(long size)
        {
            // Whatever the hash did not already account for. A stat-only decision
            // reads nothing, so without this the byte counter stalls on the cheap
            // files and leaps on the expensive ones.
            _scanned += Math.Max(0, size - _fileTicked);
            _fileTicked = size;
            _done++;
            Emit(false, "scan");
        }

        public void Flush() => Emit(true, "scan");

        public void Sweep()
        {
            _file = string.Empty;
            Emit(true, "sweep");
        }

        void Emit(bool force, string phase)
        {
            if (_sink is null)
                return;
            var now = DateTime.UtcNow;
            if (!force && (now - _last).TotalMilliseconds < IntervalMs)
                return;
            _last = now;

            _sink.Report(new ContentInstallProgress
            {
                Unit = ProgressUnit.Items,
                Phase = phase,
                Track = _track,
                Current = _done,
                Total = _files,
                ItemsDone = _done,
                ItemsTotal = _files,
                JobCurrent = _scanned,
                JobTotal = _payload,
                FileName = _file,
            });
        }
    }

    static FileAction Decide(
        ContentFile entry,
        string full,
        InstallFilesIndex? index,
        OverlayExtractPolicy overlay,
        VerifyDepth depth,
        ReconcilePlan plan,
        ScanReporter reporter,
        CancellationToken cancel,
        bool trustSize = false)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
            return WholeFetch(entry);

        if (info.Length != entry.Size)
            return Differs(entry, full, overlay, plan, reporter, cancel);

        var isOverlay = OverlayPaths.IsOverlayOwned(entry.Path);
        var mtime = info.LastWriteTimeUtc.Ticks;

        // The overlay tree is what players edit, and a same-size edit is exactly
        // what a stat sweep cannot see -- so it is always hashed.
        var mustHash = depth == VerifyDepth.Full || (depth >= VerifyDepth.Overlay && isOverlay);

        if (!mustHash)
        {
            var known = Lookup(index, entry.Path);
            if (known is not null && known.StatMatches(info.Length, mtime))
            {
                if (known.PlayerEdited)
                    return new FileAction(entry.Path, entry, FileActionKind.KeepPlayerEdit);
                if (string.Equals(known.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new FileAction(entry.Path, entry, FileActionKind.Keep);
                return Differs(entry, full, overlay, plan, reporter, cancel);
            }
            // The index was written by an older launcher that replaced it per
            // track rather than merging, so a whole track can have no records at
            // all. Re-hashing tens of GB to rediscover what the manifest already
            // says is not worth it when the tip's content is byte-identical to
            // what was installed: right size at the right path is enough, and a
            // file that is absent or truncated still gets fetched.
            if (trustSize && !isOverlay)
                return new FileAction(entry.Path, entry, FileActionKind.Keep);
            // No trustworthy record, so fall through and hash rather than guess.
        }

        var actual = HashFile(full, cancel, reporter.Tick);
        plan.Hashed++;
        plan.HashedBytes += info.Length;

        if (string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            plan.Verified[OverlayPaths.Norm(entry.Path)] = new InstallFileState
            {
                Size = info.Length,
                MTimeUtcTicks = mtime,
                Sha256 = entry.Sha256,
                VerifiedUtcTicks = DateTime.UtcNow.Ticks,
            };
            return new FileAction(entry.Path, entry, FileActionKind.Keep);
        }

        return Differs(entry, full, overlay, plan, reporter, cancel);
    }

    static FileAction Differs(
        ContentFile entry,
        string full,
        OverlayExtractPolicy overlay,
        ReconcilePlan plan,
        ScanReporter reporter,
        CancellationToken cancel)
    {
        if (overlay == OverlayExtractPolicy.KeepEdits && OverlayPaths.IsOverlayOwned(entry.Path))
            return new FileAction(entry.Path, entry, FileActionKind.KeepPlayerEdit);

        // A chunked file that is still the right size may only be wrong in a few
        // places, so a killed 10 GB transfer resumes at chunk granularity.
        if (entry.IsChunked)
        {
            try
            {
                var info = new FileInfo(full);
                if (info.Exists && info.Length == entry.Size)
                {
                    var missing = MissingChunks(entry, full, plan, reporter, cancel);
                    if (missing.Count == 0)
                        return new FileAction(entry.Path, entry, FileActionKind.Keep);
                    if (missing.Count < entry.Chunks!.Count)
                    {
                        var bytes = missing.Sum(entry.ChunkLength);
                        return new FileAction(
                            entry.Path, entry, FileActionKind.FetchChunks, missing, bytes);
                    }
                }
            }
            catch (IOException)
            {
                // Unreadable right now; fetch the whole thing rather than guess.
            }
        }

        return WholeFetch(entry);
    }

    static FileAction WholeFetch(ContentFile entry) =>
        new(entry.Path, entry, entry.IsChunked ? FileActionKind.FetchChunks : FileActionKind.Fetch,
            entry.IsChunked ? Enumerable.Range(0, entry.Chunks!.Count).ToList() : null,
            entry.Size);

    static List<int> MissingChunks(
        ContentFile entry, string full, ReconcilePlan plan, ScanReporter reporter,
        CancellationToken cancel)
    {
        var missing = new List<int>();
        using var fs = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.SequentialScan);

        var buffer = new byte[81920];
        for (var i = 0; i < entry.Chunks!.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var len = entry.ChunkLength(i);
            fs.Position = entry.ChunkOffset(i);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long left = len;
            while (left > 0)
            {
                var want = (int)Math.Min(buffer.Length, left);
                var read = fs.Read(buffer, 0, want);
                if (read <= 0)
                    break;
                hash.AppendData(buffer, 0, read);
                left -= read;
                reporter.Tick(read);
            }

            plan.HashedBytes += len - left;
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (left > 0 || !string.Equals(actual, entry.Chunks[i], StringComparison.OrdinalIgnoreCase))
                missing.Add(i);
        }
        return missing;
    }

    /// <summary>
    /// Anything under a root the manifest owns that the manifest does not list.
    /// Official leftovers (a pak the previous tip shipped and this one dropped)
    /// still go. Files the index never recorded -- custom maps in paks/, vpk/,
    /// maps/navmesh, platform/maps -- are player content, not damage.
    /// </summary>
    static void AddDeletions(
        ContentManifest manifest,
        string installPath,
        HashSet<string> wanted,
        OverlayExtractPolicy overlay,
        InstallFilesIndex? index,
        ReconcilePlan plan,
        CancellationToken cancel)
    {
        // OwnedRoots is a directory prefix, not an ownership class: the platform
        // manifest covers "platform/" but never lists the fat-owned files that
        // live there, and the client manifest covers "paks/" without listing HD
        // textures. A track may only delete classes its own manifest carries --
        // otherwise it strips another track's payload (the dedi reads
        // platform/net_prophuff.dat and AVs at map spawn without it).
        var ownedClasses = new HashSet<OverlayPaths.Class>();
        foreach (var f in manifest.Files)
            ownedClasses.Add(OverlayPaths.Classify(f.Path));

        foreach (var root in OwnedRoots(manifest))
        {
            cancel.ThrowIfCancellationRequested();
            if (!SafePath.TryJoin(installPath, root, out var dir) || !Directory.Exists(dir))
                continue;

            foreach (var file in EnumerateNoReparse(dir, cancel))
            {
                var rel = OverlayPaths.Norm(Path.GetRelativePath(installPath, file));
                if (rel.Length == 0 || wanted.Contains(rel))
                    continue;
                if (IsLauncherPrivate(rel))
                    continue;
                if (!ownedClasses.Contains(OverlayPaths.Classify(rel)))
                    continue;
                if (IsInstallScratch(rel))
                {
                    plan.Actions.Add(new FileAction(rel, null, FileActionKind.Delete));
                    continue;
                }

                // Files the game itself writes, and edits the player is allowed
                // to make, are not leftovers.
                if (OverlayPaths.IsOverlayOwned(rel) || OverlayPaths.IsOverlayOptional(rel))
                {
                    if (overlay == OverlayExtractPolicy.KeepEdits)
                    {
                        plan.Actions.Add(new FileAction(rel, null, FileActionKind.KeepPlayerEdit));
                        continue;
                    }
                }

                if (OverlayPaths.IsPlayerMap(rel) || WasNeverOfficial(index, rel))
                {
                    plan.Actions.Add(new FileAction(rel, null, FileActionKind.KeepPlayerEdit));
                    continue;
                }

                plan.Actions.Add(new FileAction(rel, null, FileActionKind.Delete));
            }
        }
    }

    /// <summary>
    /// True when this path was never part of an official install we recorded.
    /// A missing index is a first adoption: sweep extras so a leftover official
    /// pak from a previous pack is not a second copy. Once INSTALL_FILES.json
    /// exists, only paths it listed as official can be leftovers.
    /// </summary>
    static bool WasNeverOfficial(InstallFilesIndex? index, string rel)
    {
        if (index is null || index.Files.Count == 0)
            return false;
        if (!index.Files.TryGetValue(OverlayPaths.Norm(rel), out var state))
            return true;
        return state.PlayerEdited;
    }

    static bool IsLauncherPrivate(string rel) =>
        rel.StartsWith(ProductConstants.ContentCacheDirName + "/", StringComparison.OrdinalIgnoreCase) ||
        rel.EndsWith(".r5fpart", StringComparison.OrdinalIgnoreCase) ||
        rel.Equals(ProductConstants.ModsDirName, StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith(ProductConstants.ModsDirName + "/", StringComparison.OrdinalIgnoreCase);

    static bool IsInstallScratch(string rel)
    {
        var name = rel;
        var slash = rel.LastIndexOf('/');
        if (slash >= 0)
            name = rel[(slash + 1)..];
        return name.Contains(".7z.", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".zip.", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Top-level directories the manifest covers. Deletion never leaves these,
    /// so an install root that also holds unrelated user files stays intact.
    /// </summary>
    public static IReadOnlyCollection<string> OwnedRoots(ContentManifest manifest)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            var p = OverlayPaths.Norm(f.Path);
            var slash = p.IndexOf('/');
            if (slash > 0)
                roots.Add(p[..slash]);
        }
        return roots;
    }

    /// <summary>
    /// Players junction paks/ onto a second drive. Following a reparse point
    /// while collecting deletions would walk straight off the install.
    /// </summary>
    static IEnumerable<string> EnumerateNoReparse(string dir, CancellationToken cancel)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var current = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                try
                {
                    if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch
                {
                    continue;
                }
                stack.Push(sub);
            }

            foreach (var f in files)
                yield return f;
        }
    }

    static InstallFileState? Lookup(InstallFilesIndex? index, string rel)
    {
        if (index is null)
            return null;
        return index.Files.TryGetValue(OverlayPaths.Norm(rel), out var v) ? v : null;
    }

    public static string HashFile(
        string path, CancellationToken cancel = default, Action<long>? read = null)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
            read?.Invoke(n);
        }
        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }
}
