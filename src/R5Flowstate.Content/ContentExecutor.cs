using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>Why a run stopped when it was not the user's doing.</summary>
public enum InstallStopReason
{
    None = 0,
    Cancelled,
    DiskFull,
    LockedFile,
    ReadOnlyPath,
    AntivirusSuspected,
    ManifestBroken,
    Unknown,
}

public sealed class ExecuteResult
{
    public bool Success { get; set; }
    public InstallStopReason Reason { get; set; } = InstallStopReason.None;
    public string? Error { get; set; }
    public string? OffendingPath { get; set; }
    public int FilesWritten { get; set; }
    public int ObjectsFetched { get; set; }
    public long BytesFetched { get; set; }
    public int Deleted { get; set; }
}

/// <summary>
/// Applies a <see cref="ReconcilePlan"/>.
///
/// The unit of work is one object, not one file. Queueing whole files pins a
/// large file to a single connection: a 10 GB file is 640 chunks, and fetching
/// them one after another leaves the rest of the queue idle while most of the
/// bytes crawl down one stream. Splitting a file into parts is only worth
/// anything if the parts can move at the same time.
///
/// Objects are verified in memory before anything is written, so a part file
/// never holds bytes already known to be wrong. There is no download cache:
/// bytes land next to their destination as <c>.r5fpart</c> and commit with a
/// single atomic move.
/// </summary>
public static class ContentExecutor
{
    public const string PartSuffix = ".r5fpart";

    /// <summary>0 keeps the automatic choice.</summary>
    public static int GlobalConcurrency { get; set; }

    public static int DefaultConcurrency =>
        GlobalConcurrency > 0
            ? Math.Clamp(GlobalConcurrency, 1, 32)
            : Math.Clamp(Environment.ProcessorCount, 4, 8);

    sealed class FileJob
    {
        public required ContentFile Entry { get; init; }
        public required string Dest { get; init; }
        public required string Target { get; init; }
        public required bool InPlace { get; init; }
        public int Remaining;
        public bool IsPart => !InPlace && Target.EndsWith(PartSuffix, StringComparison.Ordinal);
    }

    sealed class ObjectWork
    {
        public required FileJob Job { get; init; }
        public required string Digest { get; init; }
        public int ChunkIndex { get; init; } = -1;
        public long Offset { get; init; }
        public long Length { get; init; }
    }

    public static async Task<ExecuteResult> ExecuteAsync(
        ReconcilePlan plan,
        ContentManifest manifest,
        string installPath,
        string casBaseUrl,
        FileSystemFetcher fetcher,
        int concurrency = 0,
        DownloadJournal? journal = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(fetcher);

        var result = new ExecuteResult();
        var failures = new ConcurrentQueue<(string Path, Exception Ex)>();

        List<ObjectWork> objects;
        List<FileJob> jobs;
        try
        {
            (jobs, objects) = BuildWork(plan, installPath);
        }
        catch (Exception ex)
        {
            result.Reason = Classify(ex);
            result.Error = ex.Message;
            return result;
        }

        var totalBytes = objects.Sum(o => o.Length);
        long doneBytes = 0;
        var doneFiles = 0;
        var fetched = 0;
        var lastReport = DateTime.MinValue;
        var active = new ConcurrentDictionary<string, (long Done, long Total)>();

        if (concurrency <= 0)
            concurrency = DefaultConcurrency;

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var gate = new SemaphoreSlim(concurrency);

        var tasks = objects.Select(async work =>
        {
            await gate.WaitAsync(abort.Token).ConfigureAwait(false);
            try
            {
                var rel = work.Job.Entry.Path;
                active.AddOrUpdate(rel, _ => (0, work.Job.Entry.Size),
                    (_, cur) => (cur.Done, work.Job.Entry.Size));

                var bytes = await fetcher.GetObjectAsync(
                    ObjectUrl(casBaseUrl, manifest, work.Digest),
                    work.Digest, work.Length, abort.Token,
                    n =>
                    {
                        var d = Interlocked.Add(ref doneBytes, n);
                        active.AddOrUpdate(rel, _ => (n, work.Job.Entry.Size),
                            (_, cur) => (cur.Done + n, work.Job.Entry.Size));

                        // Snapshotting on every chunk of every worker would cost
                        // more than the download.
                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds < 120)
                            return;
                        lastReport = now;

                        progress?.Report(new ContentInstallProgress
                        {
                            Unit = ProgressUnit.Bytes,
                            Phase = "fetch",
                            FileName = rel,
                            Current = d,
                            Total = totalBytes,
                            JobCurrent = d,
                            JobTotal = totalBytes,
                            ItemsDone = Volatile.Read(ref doneFiles),
                            ItemsTotal = jobs.Count,
                            StepIndex = Volatile.Read(ref doneFiles),
                            StepCount = jobs.Count,
                            Active = Snapshot(active),
                        });
                    }).ConfigureAwait(false);

                WriteAt(work.Job.Target, work.Offset, bytes, work.ChunkIndex < 0);
                Interlocked.Increment(ref fetched);
                journal?.Record(rel, work.Digest, work.ChunkIndex);

                // The last object of a file finalises it, so verifying one file
                // overlaps with downloading the next.
                if (Interlocked.Decrement(ref work.Job.Remaining) == 0)
                {
                    FinalizeFile(work.Job, abort.Token);
                    Interlocked.Increment(ref doneFiles);
                    active.TryRemove(rel, out _);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Enqueue((work.Job.Entry.Path, ex));
                abort.Cancel();
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // A worker failed; the real cause is on the queue.
        }

        result.ObjectsFetched = fetched;
        result.BytesFetched = Interlocked.Read(ref doneBytes);
        result.FilesWritten = doneFiles;

        if (cancel.IsCancellationRequested)
        {
            result.Reason = InstallStopReason.Cancelled;
            result.Error = "cancelled";
            return result;
        }

        if (failures.TryDequeue(out var first))
        {
            result.Reason = Classify(first.Ex);
            result.Error = first.Ex.Message;
            result.OffendingPath = first.Path;
            return result;
        }

        result.Deleted = ApplyDeletions(plan, installPath, progress, cancel);
        result.Success = true;
        return result;
    }

    /// <summary>
    /// Resolve every destination and size it once, up front, then expose the
    /// individual objects. Preparing a file inside the parallel loop would race
    /// two chunks of the same file both trying to create it.
    /// </summary>
    static (List<FileJob>, List<ObjectWork>) BuildWork(ReconcilePlan plan, string installPath)
    {
        var jobs = new List<FileJob>();
        var objects = new List<ObjectWork>();

        foreach (var action in plan.Work)
        {
            var entry = action.Entry
                ?? throw new InvalidOperationException($"No manifest entry for {action.Path}");
            var dest = SafePath.Join(installPath, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (!entry.IsChunked)
            {
                var whole = new FileJob
                {
                    Entry = entry, Dest = dest, Target = dest, InPlace = true, Remaining = 1,
                };
                jobs.Add(whole);
                objects.Add(new ObjectWork
                {
                    Job = whole, Digest = entry.Sha256, Offset = 0, Length = entry.Size,
                });
                continue;
            }

            var missing = action.Chunks ?? Enumerable.Range(0, entry.Chunks!.Count).ToList();
            var exists = File.Exists(dest);

            // A file that is only partly wrong is already invalid, so repairing
            // it in place risks nothing and avoids copying gigabytes. A file
            // being replaced wholesale stays good until the new one lands.
            var inPlace = exists && missing.Count < entry.Chunks!.Count;
            var target = inPlace ? dest : (exists ? dest + PartSuffix : dest);
            Prepare(target, entry.Size, inPlace);

            var job = new FileJob
            {
                Entry = entry, Dest = dest, Target = target, InPlace = inPlace,
                Remaining = missing.Count,
            };
            jobs.Add(job);

            foreach (var idx in missing)
            {
                objects.Add(new ObjectWork
                {
                    Job = job,
                    Digest = entry.Chunks![idx],
                    ChunkIndex = idx,
                    Offset = entry.ChunkOffset(idx),
                    Length = entry.ChunkLength(idx),
                });
            }
        }

        return (jobs, objects);
    }

    static void WriteAt(string target, long offset, byte[] bytes, bool wholeFile)
    {
        if (wholeFile)
        {
            File.WriteAllBytes(target, bytes);
            return;
        }

        // FileShare.ReadWrite: several chunks of the same file are in flight and
        // each writes its own region.
        using var fs = new FileStream(
            target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
            1024 * 1024, FileOptions.WriteThrough);
        fs.Position = offset;
        fs.Write(bytes, 0, bytes.Length);
    }

    static void FinalizeFile(FileJob job, CancellationToken cancel)
    {
        // Per-chunk hashes cannot catch a correct chunk written to the wrong
        // offset. This is the only check that does.
        var whole = ContentReconciler.HashFile(job.Target, cancel);
        if (!string.Equals(whole, job.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (job.IsPart)
                TryDelete(job.Target);
            throw new IOException($"Assembled file does not match the manifest: {job.Entry.Path}");
        }

        if (job.IsPart)
            File.Move(job.Target, job.Dest, overwrite: true);
    }

    /// <summary>
    /// Size the destination up front. Sparse first: without it, writing at a high
    /// offset makes NTFS synchronously zero-fill everything before it, which on a
    /// 10 GB file is a multi-minute stall that looks like a hang.
    /// </summary>
    static void Prepare(string target, long size, bool inPlace)
    {
        if (inPlace && File.Exists(target))
            return;

        using var fs = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
        TrySetSparse(fs);
        fs.SetLength(size);
    }

    const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(
        SafeHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    static void TrySetSparse(FileStream fs)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            // ReFS and some compression combinations refuse; writes still work,
            // they just pay the zero-fill.
        }
    }

    static List<ActiveTransfer> Snapshot(
        ConcurrentDictionary<string, (long Done, long Total)> active)
    {
        var list = new List<ActiveTransfer>(active.Count);
        foreach (var kv in active)
            list.Add(new ActiveTransfer { Path = kv.Key, Done = kv.Value.Done, Total = kv.Value.Total });
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    static int ApplyDeletions(
        ReconcilePlan plan,
        string installPath,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        var n = 0;
        foreach (var action in plan.Deletions)
        {
            cancel.ThrowIfCancellationRequested();
            if (!SafePath.TryJoin(installPath, action.Path, out var full))
                continue;
            try
            {
                if (File.Exists(full))
                {
                    File.SetAttributes(full, FileAttributes.Normal);
                    File.Delete(full);
                    n++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover we cannot remove is not worth failing an install
                // over; the next reconcile pass tries again.
                progress?.Report(new ContentInstallProgress
                {
                    Phase = "delete",
                    Message = "could not remove " + action.Path,
                });
            }
        }
        return n;
    }

    static string ObjectUrl(string baseUrl, ContentManifest manifest, string digest)
    {
        var key = manifest.ObjectKey(digest);
        return string.IsNullOrWhiteSpace(baseUrl) ? key : baseUrl.TrimEnd('/') + "/" + key;
    }

    static InstallStopReason Classify(Exception ex)
    {
        if (ex is IOException io)
        {
            var code = io.HResult & 0xFFFF;
            if (code == 112)
                return InstallStopReason.DiskFull;
            if (code == 32 || code == 33)
                return InstallStopReason.LockedFile;
        }
        if (ex is UnauthorizedAccessException)
            return InstallStopReason.ReadOnlyPath;
        if (ex is InvalidOperationException &&
            ex.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase))
        {
            return InstallStopReason.ManifestBroken;
        }
        return InstallStopReason.Unknown;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
