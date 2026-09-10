using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Installs one track from the content-addressed store. Returns the same
/// <see cref="TrackInstallResult"/> the volume installer does, so the surrounding
/// orchestration -- track sequencing, per-step INSTALL_STATE checkpoints,
/// overlay policy, finalisation -- is unchanged.
/// </summary>
public static class ContentTrackInstaller
{
    /// <summary>
    /// Reconcile, apply, reconcile again. Settlement uses the index; it does
    /// not re-hash the tree.
    /// </summary>
    public const int MaxPasses = 3;

    public static async Task<TrackInstallResult> InstallTrackAsync(
        string preset,
        UpdateStep step,
        string installPath,
        string casBaseUrl,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        OverlayExtractPolicy overlay = OverlayExtractPolicy.WriteOfficial,
        int concurrency = 0,
        bool contentUnchanged = false)
    {
        var result = new TrackInstallResult
        {
            Preset = preset,
            CatalogVersion = step.ToVersion,
            ContentHash = step.ContentHash ?? string.Empty,
        };

        try
        {
            var manifest = await LoadManifestAsync(
                step, installPath, preset, fetcher, progress, cancel).ConfigureAwait(false);

            var indexPath = Path.Combine(installPath, ProductConstants.InstallFilesFileName);
            var journalPath = Path.Combine(
                installPath, ProductConstants.ContentCacheDirName, "journal.tsv");

            var direct = fetcher as FileSystemFetcher ?? new FileSystemFetcher();
            var ownsFetcher = !ReferenceEquals(direct, fetcher);

            try
            {
                for (var pass = 1; pass <= MaxPasses; pass++)
                {
                    cancel.ThrowIfCancellationRequested();

                    var index = InstallFilesIndexIO.TryLoad(indexPath);

                    progress?.Report(new ContentInstallProgress
                    {
                        Unit = ProgressUnit.Items,
                        Phase = "scan",
                        Track = preset,
                        Total = manifest.Files.Count,
                        ItemsTotal = manifest.Files.Count,
                        JobTotal = manifest.PayloadBytes,
                        Message = pass == 1 ? "checking installed files" : $"verify pass {pass}",
                    });

                    // Hashing tens of GB is the longest synchronous stretch in an
                    // install. A caller whose awaits all completed inline would
                    // otherwise run it on its own thread, which for the shell is
                    // the UI thread -- the scan is exactly when the window must
                    // stay alive.
                    var plan = await Task.Run(
                        () => ContentReconciler.Plan(
                            manifest, index, installPath, overlay, VerifyDepth.Overlay,
                            progress, cancel,
                            p => Checkpoint(indexPath, manifest, installPath, p),
                            preset, contentUnchanged),
                        cancel).ConfigureAwait(false);

                    result.LastScanHashed = plan.Hashed;
                    result.VerifyPasses = pass;

                    if (plan.IsConverged)
                    {
                        WriteIndex(indexPath, manifest, installPath, plan);
                        DownloadJournal.Clear(journalPath);

                        // The archive installer's scratch can be tens of GB that
                        // will never be read again, and a player arriving from
                        // the old launcher has no way to know it is there.
                        var swept = LegacyCleanup.Run(installPath, progress);
                        if (swept.FilesRemoved > 0)
                        {
                            progress?.Report(new ContentInstallProgress
                            {
                                Phase = "cleanup",
                                Track = preset,
                                Message =
                                    $"reclaimed {swept.BytesReclaimed / 1_000_000_000.0:F1} GB " +
                                    $"of leftover install files",
                            });
                        }
                        result.ContentHash = manifest.ContentHash;
                        result.CatalogVersion = manifest.CatalogVersion;
                        result.Success = true;
                        progress?.Report(new ContentInstallProgress
                        {
                            Phase = "done",
                            Track = preset,
                            Current = 1,
                            Total = 1,
                            Message = $"track {preset} ok (passes={pass})",
                        });
                        return result;
                    }

                    if (plan.DeletionsLookWrong(manifest.Files.Count))
                    {
                        result.Error = plan.DescribeDeletionRefusal(manifest.Files.Count);
                        return result;
                    }

                    // Checkpoint is every 1500 hashed files. Flush the remainder
                    // so the next pass can skip them.
                    Checkpoint(indexPath, manifest, installPath, plan);

                    // Now the real download size is known, unlike at plan time
                    // where the tip only carries the size of the whole install.
                    var need = plan.BytesToFetch;
                    if (need > 0)
                    {
                        var free = InstallPathPolicy.FreeBytes(installPath);
                        var headroom = InstallPathPolicy.RequiredFreeBytesInPlace(need);
                        if (free >= 0 && free < headroom)
                        {
                            result.Error =
                                $"Not enough free space: {headroom / 1_000_000_000.0:F1} GB " +
                                $"needed, {free / 1_000_000_000.0:F1} GB available.";
                            return result;
                        }
                    }

                    using var journal = DownloadJournal.Open(journalPath);
                    var exec = await ContentExecutor.ExecuteAsync(
                        plan, manifest, installPath, casBaseUrl, direct,
                        concurrency, journal, progress, cancel).ConfigureAwait(false);

                    result.VolumeRegets += exec.ObjectsFetched;

                    if (!exec.Success)
                    {
                        result.Error = Describe(exec);
                        return result;
                    }

                    RecordApplied(indexPath, manifest, installPath, plan);
                }

                result.Error =
                    $"Track '{preset}' did not settle after {MaxPasses} passes; " +
                    "files are changing underneath the installer.";
                return result;
            }
            finally
            {
                if (ownsFetcher)
                    direct.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            return result;
        }
    }

    static async Task<ContentManifest> LoadManifestAsync(
        UpdateStep step,
        string installPath,
        string preset,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(step.ContentManifestUrl))
            throw new InvalidOperationException($"Track '{preset}' has no content_manifest_url.");

        var dir = Path.Combine(installPath, ProductConstants.ContentCacheDirName, "manifests");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Sanitize($"{preset}_{step.ToVersion}.json"));

        progress?.Report(new ContentInstallProgress
        {
            Phase = "manifest",
            Track = preset,
            Message = "content manifest",
        });

        await fetcher.DownloadAsync(
            step.ContentManifestUrl, dest, step.ContentManifestSha256, progress, cancel)
            .ConfigureAwait(false);

        // Verified against the digest the CHANNEL carries, then shape-checked
        // before a single object URL is derived from it.
        return ContentManifestIO.LoadVerified(dest, step.ContentManifestSha256);
    }

    /// <summary>
    /// Fold what the scan has confirmed so far into the index on disk. The
    /// first run against an install made by the volume-era launcher hashes
    /// the whole tree; without this, closing the launcher part way through
    /// would mean doing all of it again.
    /// </summary>
    static void Checkpoint(
        string path, ContentManifest manifest, string installPath, ReconcilePlan plan)
    {
        try
        {
            var index = InstallFilesIndexIO.TryLoad(path) ?? new InstallFilesIndex
            {
                ManifestId = manifest.ManifestId,
                InstallPath = installPath,
            };
            foreach (var kv in plan.Verified)
                index.Files[kv.Key] = kv.Value;
            InstallFilesIndexIO.Save(path, index);
        }
        catch
        {
            // A checkpoint that cannot be written costs time on the next run
            // and nothing else.
        }
    }

    static void WriteIndex(
        string path, ContentManifest manifest, string installPath, ReconcilePlan plan)
    {
        var edited = new HashSet<string>(
            plan.PlayerEdits.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);

        // One index file serves every track, so starting fresh here drops the
        // other tracks' records and the next reconcile hashes their whole tree
        // instead of stat-comparing it -- 40 GB of client payload re-read after
        // a platform install.
        var index = InstallFilesIndexIO.TryLoad(path) ?? new InstallFilesIndex();
        index.ManifestId = manifest.ManifestId;
        index.InstallPath = installPath;
        index.VerifiedUtc = DateTime.UtcNow.ToString("O");

        var now = DateTime.UtcNow.Ticks;
        foreach (var f in manifest.Files)
        {
            if (!SafePath.TryJoin(installPath, f.Path, out var full))
                continue;
            var info = new FileInfo(full);
            if (!info.Exists)
                continue;

            var isEdit = edited.Contains(f.Path);
            index.Files[OverlayPaths.Norm(f.Path)] = new InstallFileState
            {
                Size = info.Length,
                MTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
                // A kept edit is deliberately not the manifest hash; recording it
                // as such would make the next launch call it corruption.
                Sha256 = isEdit ? null : f.Sha256,
                VerifiedUtcTicks = now,
                PlayerEdited = isEdit,
            };
        }

        InstallFilesIndexIO.Save(path, index);
    }

    /// <summary>
    /// FinalizeFile already hashed every written file. Recording the current
    /// mtime is what lets the next pass skip them instead of hashing the tree.
    /// </summary>
    static void RecordApplied(
        string path, ContentManifest manifest, string installPath, ReconcilePlan plan)
    {
        try
        {
            var index = InstallFilesIndexIO.TryLoad(path) ?? new InstallFilesIndex
            {
                ManifestId = manifest.ManifestId,
                InstallPath = installPath,
            };
            index.ManifestId = manifest.ManifestId;
            index.InstallPath = installPath;

            foreach (var kv in plan.Verified)
                index.Files[kv.Key] = kv.Value;

            var now = DateTime.UtcNow.Ticks;
            foreach (var action in plan.Work)
            {
                if (action.Entry is null)
                    continue;
                if (!SafePath.TryJoin(installPath, action.Path, out var full))
                    continue;
                var info = new FileInfo(full);
                if (!info.Exists || info.Length != action.Entry.Size)
                    continue;
                index.Files[OverlayPaths.Norm(action.Path)] = new InstallFileState
                {
                    Size = info.Length,
                    MTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = action.Entry.Sha256,
                    VerifiedUtcTicks = now,
                };
            }

            foreach (var action in plan.Deletions)
                index.Files.Remove(OverlayPaths.Norm(action.Path));

            InstallFilesIndexIO.Save(path, index);
        }
        catch
        {
            // Same as Checkpoint: a missing index costs time on the next pass.
        }
    }

    static string Describe(ExecuteResult exec) => exec.Reason switch
    {
        InstallStopReason.DiskFull =>
            "Not enough free space to finish. Free some space and resume.",
        InstallStopReason.LockedFile =>
            $"A file is in use by another program: {exec.OffendingPath}",
        InstallStopReason.ReadOnlyPath =>
            $"No permission to write {exec.OffendingPath}",
        InstallStopReason.AntivirusSuspected =>
            $"Antivirus appears to be removing {exec.OffendingPath} as it is written.",
        InstallStopReason.ManifestBroken =>
            exec.Error ?? "The published content manifest is inconsistent.",
        _ => exec.Error ?? "unknown",
    };

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
