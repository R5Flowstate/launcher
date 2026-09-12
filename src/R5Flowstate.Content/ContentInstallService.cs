using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public static class ContentInstallService
{
    public static InstallHealthReport Assess(
        ChannelManifest? channel,
        string installPath,
        bool requireClient = true,
        bool requireServer = false) =>
        InstallHealthAssessor.Assess(channel, installPath, requireClient, requireServer);

    /// <summary>
    /// User-facing check of an already-on-disk folder: INSTALL_STATE, triad, SHARE lists.
    /// Reports progress. Does not download or repair.
    /// </summary>
    public static Task<InstallHealthReport> VerifyExistingAsync(
        ChannelManifest? channel,
        string installPath,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default) =>
        Task.Run(() => VerifyExisting(channel, installPath, progress, cancel), cancel);

    static InstallHealthReport VerifyExisting(
        ChannelManifest? channel,
        string installPath,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        void Step(string message, long current = 0, long total = 0, string track = "")
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new ContentInstallProgress
            {
                Phase = "check",
                Track = track,
                Current = current,
                Total = total,
                Message = message,
            });
        }

        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            Step("Folder not found.");
            return Assess(channel, installPath ?? string.Empty, true, true);
        }

        Step("Reading INSTALL_STATE…");
        var statePath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
        InstallState? state = null;
        if (File.Exists(statePath))
        {
            try
            {
                state = InstallStateIO.Load(statePath);
                Step("INSTALL_STATE ok.");
            }
            catch (Exception ex)
            {
                Step("INSTALL_STATE unreadable: " + ex.Message);
            }
        }
        else
        {
            Step("No INSTALL_STATE — checking game files…");
        }

        var probes = new (string label, string rel)[]
        {
            ("client exe", "r5apex.exe"),
            ("loader", "loader.dll"),
            ("client dll", "client.dll"),
            ("client dll", Path.Combine("game", "client.dll")),
            ("server exe", "r5apex_ds.exe"),
            ("server dll", "server.dll"),
            ("server dll", Path.Combine("game", "server.dll")),
            ("playlists", Path.Combine("platform", "playlists_r5_patch.txt")),
        };
        for (var i = 0; i < probes.Length; i++)
        {
            var (label, rel) = probes[i];
            var full = Path.Combine(installPath, rel);
            var present = File.Exists(full);
            Step(
                present ? "Found " + label + "." : "Missing " + label + ".",
                i + 1,
                probes.Length,
                "probe");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (preset, manPath) in EnumerateShareManifests(installPath, state))
        {
            if (!seen.Add(manPath))
                continue;
            Step("Reading " + preset + " manifest…", track: preset);
            try
            {
                var man = ShareManifestIO.Load(manPath);
                // SkipFatVerify means "not this track's payload", so only the fat
                // tracks may use it as a skip. Platform and HD own nothing else,
                // and skipping their own classes would verify an empty set.
                var ownsNonFat =
                    string.Equals(preset, "platform", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(preset, "hd", StringComparison.OrdinalIgnoreCase);
                var fileProgress = new Progress<(int current, int total, string path)>(p =>
                {
                    Step(
                        "Checking " + preset + " files…",
                        p.current,
                        p.total,
                        preset);
                });
                ShareFileVerifier.VerifyInstallFiles(
                    installPath,
                    man,
                    skipRelativePath: ownsNonFat ? null : OverlayPaths.SkipFatVerify,
                    progress: fileProgress,
                    sizeOptionalRelativePath: ownsNonFat ? OverlayPaths.SkipFatVerify : null);
            }
            catch (Exception ex)
            {
                Step(preset + " manifest failed: " + ex.Message, track: preset);
            }
        }

        Step("Finishing check…");
        return Assess(channel, installPath, requireClient: true, requireServer: true);
    }

    static IEnumerable<(string preset, string path)> EnumerateShareManifests(
        string installPath,
        InstallState? state)
    {
        foreach (var (preset, ver) in new[]
        {
            ("client", state?.ClientCatalogVersion),
            ("server", state?.ServerCatalogVersion),
            ("platform", state?.PlatformCatalogVersion),
            ("hd", state?.HdCatalogVersion),
        })
        {
            var cached = InstallHealthAssessor.FindCachedShareManifest(installPath, preset, ver);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
                yield return (preset, cached);
        }

        var last = OverlayLeftoverWipe.LastSharePath(installPath);
        if (File.Exists(last))
            yield return ("overlay", last);

        var root = Path.Combine(installPath, ProductConstants.ShareManifestFileName);
        if (File.Exists(root))
            yield return ("root", root);
    }

    /// <summary>
    /// Auto-repair corrupted tracks; refuse silent update (caller must Install Full for updates).
    /// Returns final health after optional repair.
    /// </summary>
    public static async Task<InstallHealthReport> EnsureReadyAsync(
        ChannelManifest channel,
        string installPath,
        bool requireClient,
        bool requireServer,
        bool autoRepairCorrupt = true,
        bool autoApplyUpdates = false,
        IHttpFetcher? fetcher = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        Func<OverlayEditReport, OverlayExtractPolicy>? decideOverlay = null,
        bool allowContent = true,
        bool allowPlatform = true)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var report = Assess(channel, installPath, requireClient, requireServer);
        if (report.IsReady || !report.Enforced)
            return report;

        if (report.NeedsUpdate && !autoApplyUpdates)
            return report;

        if (report.NeedsRepair && autoRepairCorrupt)
        {
            progress?.Report(new ContentInstallProgress
            {
                Phase = "repair",
                Message = "auto-repair corrupted install: " + report.Summary,
            });

            var mode = PickRepairMode(requireClient, requireServer, report);
            // The files are known broken -- that is why this ran -- so the
            // recorded version must not be allowed to short-circuit the work.
            await InstallAsync(channel, mode, installPath, fetcher, progress, cancel,
                    decideOverlay: decideOverlay,
                    allowContent: allowContent,
                    allowPlatform: allowPlatform,
                    forceReinstall: true)
                .ConfigureAwait(false);
            return Assess(channel, installPath, requireClient, requireServer);
        }

        if ((report.Overall is InstallHealthStatus.Missing or InstallHealthStatus.Incomplete)
            && autoApplyUpdates)
        {
            var mode = requireClient
                ? (requireServer ? InstallMode.Full : InstallMode.ClientOnly)
                : InstallMode.DedicatedOnly;
            await InstallAsync(channel, mode, installPath, fetcher, progress, cancel,
                    decideOverlay: decideOverlay,
                    allowContent: allowContent,
                    allowPlatform: allowPlatform)
                .ConfigureAwait(false);
            return Assess(channel, installPath, requireClient, requireServer);
        }

        if (report.NeedsUpdate && autoApplyUpdates)
        {
            var mode = requireClient
                ? (requireServer ? InstallMode.Full : InstallMode.ClientOnly)
                : InstallMode.DedicatedOnly;
            await InstallAsync(channel, mode, installPath, fetcher, progress, cancel,
                    decideOverlay: decideOverlay,
                    allowContent: allowContent,
                    allowPlatform: allowPlatform)
                .ConfigureAwait(false);
            return Assess(channel, installPath, requireClient, requireServer);
        }

        return report;
    }

    static InstallMode PickRepairMode(bool requireClient, bool requireServer, InstallHealthReport report)
    {
        var clientBad = requireClient &&
                        report.Client is not null &&
                        report.Client.Status is InstallHealthStatus.Corrupted
                            or InstallHealthStatus.Incomplete
                            or InstallHealthStatus.Missing;
        var serverBad = requireServer &&
                        report.Server is not null &&
                        report.Server.Status is InstallHealthStatus.Corrupted
                            or InstallHealthStatus.Incomplete
                            or InstallHealthStatus.Missing;

        if (clientBad && serverBad)
            return InstallMode.Full;
        if (clientBad)
            return InstallMode.ClientOnly;
        if (serverBad)
            return InstallMode.DedicatedOnly;
        return InstallMode.Full;
    }

    public static async Task<InstallState> InstallAsync(
        ChannelManifest channel,
        InstallMode mode,
        string installPath,
        IHttpFetcher? fetcher = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        InstallRunControl? runControl = null,
        Func<OverlayEditReport, OverlayExtractPolicy>? decideOverlay = null,
        bool allowContent = true,
        bool allowPlatform = true,
        bool forceReinstall = false)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var hdOptIn = TryLoadState(installPath)?.HdEnabled ?? false;
        var plan = InstallPlanner.Build(channel, mode, installPath, includeHd: hdOptIn);
        InstallPlanner.FilterDownloadLanes(plan, allowContent, allowPlatform);
        if (plan.Tracks.Count == 0)
        {
            progress?.Report(new ContentInstallProgress
            {
                Phase = "plan",
                Message = "download lanes off; nothing to fetch",
            });
            var idlePath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
            return LoadOrCreateState(idlePath, installPath, channel.EffectiveGateName);
        }

        Directory.CreateDirectory(installPath);
        Directory.CreateDirectory(plan.CacheRoot);

        // One writer per install directory. Two reconcilers on one tree corrupt
        // a part file quietly, and SingleInstance does not cover the cases that
        // produce a second one.
        using var dirLock = InstallDirectoryLock.TryAcquire(installPath, out var holder);
        if (dirLock is null)
        {
            var busyPath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
            var busy = LoadOrCreateState(busyPath, installPath, channel.EffectiveGateName);
            busy.Incomplete = true;
            busy.LastError = $"Another launcher is using this folder ({holder}).";
            InstallStateIO.Save(busyPath, busy);
            return busy;
        }

        // Discovering a shortfall part-way through a multi-hour download is the
        // worst time to find out, so this is a stop rather than a warning.
        //
        // Content tracks are excluded: their tip carries the size of the whole
        // install, not of what will be fetched, so an adoption that downloads
        // nothing would be refused on a nearly full disk. ContentTrackInstaller
        // checks them once the reconcile knows the real number.
        var archiveTracks = plan.Tracks
            .Where(t => !(GetTip(channel, t.Preset)?.UsesContentManifest ?? false))
            .ToList();
        var free = InstallPathPolicy.FreeBytes(installPath);
        var largest = archiveTracks.Count == 0 ? 0 : archiveTracks.Max(t => t.TotalBytes);
        var planned = archiveTracks.Sum(t => t.TotalBytes);
        var required = InstallPathPolicy.RequiredFreeBytes(planned, largest);
        if (free >= 0 && planned > 0 && free < required)
        {
            var shortPath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
            var shortState = LoadOrCreateState(shortPath, installPath, channel.EffectiveGateName);
            shortState.Incomplete = true;
            shortState.LastError =
                $"Not enough free space: {required / 1_000_000_000.0:F1} GB needed, " +
                $"{free / 1_000_000_000.0:F1} GB available.";
            InstallStateIO.Save(shortPath, shortState);
            progress?.Report(new ContentInstallProgress
            {
                Phase = "failed",
                Message = shortState.LastError,
            });
            return shortState;
        }

        var statePath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
        var state = LoadOrCreateState(statePath, installPath, channel.EffectiveGateName);

        var preserveClientReady =
            mode is InstallMode.ServerSecondary or InstallMode.DedicatedOnly;
        var priorClientReady = state.ClientReady;
        var priorClientCatalog = state.ClientCatalogVersion;
        var priorClientHash = state.ClientContentHash;
        var priorClientIncomplete = state.Incomplete;
        var priorClientLastStep = state.ClientLastCompletedStep;

        // Capture resolve inputs before marking incomplete (resume / already-at-tip).
        var priorServerReady = state.ServerReady;
        var priorServerCatalog = state.ServerCatalogVersion;
        var priorServerHash = state.ServerContentHash;
        var priorServerLastStep = state.ServerLastCompletedStep;
        var priorPlatformReady = state.PlatformReady;
        var priorPlatformCatalog = state.PlatformCatalogVersion;
        var priorPlatformHash = state.PlatformContentHash;
        var priorPlatformLastStep = state.PlatformLastCompletedStep;
        var priorHdReady = state.HdReady;
        var priorHdCatalog = state.HdCatalogVersion;
        var priorHdHash = state.HdContentHash;
        var priorHdLastStep = state.HdLastCompletedStep;
        var fatUnpackedThisSession = false;
        var reportedRememberedOverlay = false;
        var overlayEdits = OverlayEditProbe.Scan(installPath, channel);
        var overlayKeepKey = overlayEdits.HasEdits
            ? string.Join("|",
                overlayEdits.Fingerprint(),
                channel.Platform?.ContentHash ?? string.Empty,
                channel.Client?.ContentHash ?? string.Empty)
            : null;
        var overlayRemembered =
            overlayKeepKey is not null &&
            string.Equals(state.OverlayKeepKey, overlayKeepKey, StringComparison.Ordinal);
        OverlayExtractPolicy? overlayPolicy =
            overlayRemembered ? OverlayExtractPolicy.KeepEdits : null;

        state.InstallPath = installPath;
        state.SdkVersionExpected = channel.EffectiveGateName;
        state.Incomplete = true;
        state.LastError = null;

        if (!preserveClientReady)
        {
            if (plan.Tracks.Any(t => IsClient(t.Preset)))
                state.ClientReady = false;
        }
        else
        {
            state.ClientReady = priorClientReady;
            state.ClientCatalogVersion = priorClientCatalog;
            state.ClientContentHash = priorClientHash;
        }

        if (plan.Tracks.Any(t => IsServer(t.Preset)))
            state.ServerReady = false;
        if (plan.Tracks.Any(t => IsPlatform(t.Preset)))
            state.PlatformReady = false;
        if (plan.Tracks.Any(t => IsHd(t.Preset)))
            state.HdReady = false;

        InstallStateIO.Save(statePath, state);

        progress?.Report(new ContentInstallProgress
        {
            Phase = "plan",
            Message = $"mode={mode}; tracks={plan.Tracks.Count}",
            Total = plan.Tracks.Count,
        });

        var ownsFetcher = fetcher is null;
        fetcher ??= new FileSystemFetcher();
        if (fetcher is FileSystemFetcher fs && fs.RunControl is null)
            fs.RunControl = runControl;
        var completedClient = preserveClientReady && priorClientReady;
        var completedServer = false;
        var completedPlatform = false;
        string? failError = null;

        try
        {
            for (var i = 0; i < plan.Tracks.Count; i++)
            {
                if (runControl is not null)
                    await runControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                var track = plan.Tracks[i];
                var tip = GetTip(channel, track.Preset);
                if (tip is null)
                {
                    failError = $"CHANNEL has no tip for track '{track.Preset}'.";
                    break;
                }

                progress?.Report(new ContentInstallProgress
                {
                    Phase = "track",
                    Track = track.Preset,
                    Current = i,
                    Total = plan.Tracks.Count,
                    Message = $"install {track.Preset}",
                });

                // Ready identity is enough to skip a CAS track. Incomplete is the
                // whole job; using it here re-scanned the client after a platform miss.
                string? resolveVer;
                string? resolveHash;
                string? lastCompleted;
                if (IsClient(track.Preset))
                {
                    var ready = !forceReinstall && priorClientReady;
                    resolveVer = ready ? priorClientCatalog : null;
                    resolveHash = ready ? priorClientHash : null;
                    lastCompleted = ready ? null : priorClientLastStep;
                    if (ready)
                        state.ClientLastCompletedStep = null;
                }
                else if (IsServer(track.Preset))
                {
                    var ready = !forceReinstall && priorServerReady && !priorClientIncomplete;
                    resolveVer = ready ? priorServerCatalog : null;
                    resolveHash = ready ? priorServerHash : null;
                    lastCompleted = ready ? null : priorServerLastStep;
                    if (ready)
                        state.ServerLastCompletedStep = null;
                }
                else if (IsPlatform(track.Preset))
                {
                    var force = fatUnpackedThisSession;
                    var ready = !forceReinstall && priorPlatformReady && !force;
                    resolveVer = ready ? priorPlatformCatalog : null;
                    resolveHash = ready ? priorPlatformHash : null;
                    lastCompleted = ready ? null : (force ? null : priorPlatformLastStep);
                    if (ready)
                        state.PlatformLastCompletedStep = null;
                }
                else if (IsHd(track.Preset))
                {
                    var force = fatUnpackedThisSession;
                    var ready = !forceReinstall && priorHdReady && !force;
                    resolveVer = ready ? priorHdCatalog : null;
                    resolveHash = ready ? priorHdHash : null;
                    lastCompleted = ready ? null : (force ? null : priorHdLastStep);
                    if (ready)
                        state.HdLastCompletedStep = null;
                }
                else
                {
                    failError = $"Unknown track preset '{track.Preset}'.";
                    break;
                }

                UpdatePlan updatePlan;
                try
                {
                    // ValidateChain runs inside Resolve — before any fetch.
                    updatePlan = UpdatePlanner.Resolve(
                        tip, resolveVer, resolveHash, channel.BaseUrl);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failError = ex.Message;
                    break;
                }

                progress?.Report(new ContentInstallProgress
                {
                    Phase = "update_plan",
                    Track = track.Preset,
                    Message =
                        $"steps={updatePlan.Steps.Count} planned_bytes={updatePlan.PlannedBytes} " +
                        $"from={(resolveVer ?? "(none)")}",
                });

                if (updatePlan.Steps.Count == 0)
                {
                    // Already at tip: mark ready without fetching.
                    ApplyTrackSuccess(state, new TrackInstallResult
                    {
                        Preset = track.Preset,
                        Success = true,
                        CatalogVersion = tip.CatalogVersion,
                        ContentHash = tip.ContentHash,
                    });
                    ClearLastCompleted(state, track.Preset);
                    if (IsClient(track.Preset))
                        completedClient = true;
                    if (IsServer(track.Preset))
                        completedServer = true;
                    if (IsPlatform(track.Preset))
                        completedPlatform = true;
                    InstallStateIO.Save(statePath, state);
                    continue;
                }

                var steps = updatePlan.Steps;
                var startAt = 0;
                if (!string.IsNullOrWhiteSpace(lastCompleted))
                {
                    var idx = steps.FindIndex(s =>
                        string.Equals(s.StepId, lastCompleted, StringComparison.Ordinal));
                    if (idx >= 0)
                        startAt = idx + 1;
                }

                if (startAt >= steps.Count)
                {
                    var installedHash = InstalledHashFor(state, track.Preset);
                    if (string.IsNullOrWhiteSpace(tip.ContentHash) ||
                        !string.Equals(installedHash, tip.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        startAt = 0;
                    }
                }

                var stepFailed = false;
                for (var si = startAt; si < steps.Count; si++)
                {
                    if (runControl is not null)
                        await runControl.WaitIfPausedAsync(cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    var step = steps[si];
                    progress?.Report(new ContentInstallProgress
                    {
                        Phase = step.Kind == UpdateStepKind.InstallBase ? "base" : "patch",
                        Track = track.Preset,
                        Current = si,
                        Total = steps.Count,
                        Message = step.StepId,
                    });

                    if (overlayPolicy is null &&
                        overlayEdits.HasEdits &&
                        (step.Kind == UpdateStepKind.InstallBase ||
                         step.Kind == UpdateStepKind.SyncFiles))
                    {
                        overlayPolicy = decideOverlay?.Invoke(overlayEdits)
                                        ?? OverlayExtractPolicy.KeepEdits;
                        state.OverlayKeepKey =
                            overlayPolicy == OverlayExtractPolicy.KeepEdits
                                ? overlayKeepKey
                                : null;
                        InstallStateIO.Save(statePath, state);
                        progress?.Report(new ContentInstallProgress
                        {
                            Phase = "overlay",
                            Message = overlayPolicy == OverlayExtractPolicy.KeepEdits
                                ? "Leaving edited script files in place."
                                : "Restoring official script files.",
                        });
                    }
                    else if (overlayRemembered && !reportedRememberedOverlay &&
                             (step.Kind == UpdateStepKind.InstallBase ||
                              step.Kind == UpdateStepKind.SyncFiles))
                    {
                        reportedRememberedOverlay = true;
                        progress?.Report(new ContentInstallProgress
                        {
                            Phase = "overlay",
                            Message =
                                $"Leaving {overlayEdits.Total} edited file(s) in place " +
                                "(remembered choice).",
                        });
                    }

                    var overlay = overlayPolicy ?? OverlayExtractPolicy.KeepEdits;
                    TrackInstallResult stepResult;
                    if (step.Kind == UpdateStepKind.SyncFiles)
                    {
                        // A version-only tip bump leaves content_hash alone. The
                        // files that are already there are the files the tip
                        // wants, so the scan can trust size and skip re-hashing
                        // the whole tree -- which on an index written by an older
                        // launcher means tens of GB for nothing.
                        var installedHash = InstalledHashFor(state, track.Preset);
                        var contentUnchanged =
                            !string.IsNullOrWhiteSpace(installedHash) &&
                            !string.IsNullOrWhiteSpace(step.ContentHash) &&
                            string.Equals(installedHash, step.ContentHash,
                                StringComparison.OrdinalIgnoreCase);
                        if (contentUnchanged)
                        {
                            progress?.Report(new ContentInstallProgress
                            {
                                Phase = "scan",
                                Track = track.Preset,
                                Message = "content unchanged since last install; checking files by size",
                            });
                        }

                        stepResult = await ContentTrackInstaller.InstallTrackAsync(
                            track.Preset,
                            step,
                            plan.InstallPath,
                            step.CasBaseUrl ?? channel.BaseUrl ?? string.Empty,
                            fetcher,
                            progress,
                            cancel,
                            overlay,
                            contentUnchanged: contentUnchanged).ConfigureAwait(false);
                    }
                    else if (step.Kind == UpdateStepKind.InstallBase)
                    {
                        stepResult = await InstallBaseStepAsync(
                            track.Preset,
                            step,
                            plan.InstallPath,
                            plan.CacheRoot,
                            fetcher,
                            progress,
                            cancel,
                            overlay).ConfigureAwait(false);
                    }
                    else
                    {
                        stepResult = await PatchApplier.ApplyPatchAsync(
                            track.Preset,
                            step,
                            plan.InstallPath,
                            plan.CacheRoot,
                            fetcher,
                            progress,
                            cancel,
                            overlay).ConfigureAwait(false);
                    }

                    if (!stepResult.Success)
                    {
                        failError =
                            $"Step '{step.StepId}' failed: {stepResult.Error ?? "unknown"}";
                        stepFailed = true;
                        break;
                    }

                    if (step.Kind == UpdateStepKind.InstallBase &&
                        overlay != OverlayExtractPolicy.KeepEdits &&
                        (IsPlatform(track.Preset) || IsClient(track.Preset)))
                    {
                        TryPlatformLeftoverWipe(plan.InstallPath, track.Preset, step.ToVersion);
                    }

                    if (IsClient(track.Preset) || IsServer(track.Preset))
                        fatUnpackedThisSession = true;

                    SetLastCompleted(state, track.Preset, step.StepId);
                    // Intermediate catalog after each successful step; ready only at chain end.
                    if (IsClient(track.Preset))
                    {
                        state.ClientCatalogVersion = step.ToVersion;
                        state.ClientContentHash = step.ContentHash
                                                  ?? stepResult.ContentHash
                                                  ?? string.Empty;
                        state.ClientReady = false;
                    }
                    else if (IsServer(track.Preset))
                    {
                        state.ServerCatalogVersion = step.ToVersion;
                        state.ServerContentHash = step.ContentHash
                                                  ?? stepResult.ContentHash
                                                  ?? string.Empty;
                        state.ServerReady = false;
                    }
                    else if (IsPlatform(track.Preset))
                    {
                        state.PlatformCatalogVersion = step.ToVersion;
                        state.PlatformContentHash = step.ContentHash
                                                    ?? stepResult.ContentHash
                                                    ?? string.Empty;
                        state.PlatformReady = false;
                    }
                    else if (IsHd(track.Preset))
                    {
                        state.HdCatalogVersion = step.ToVersion;
                        state.HdContentHash = step.ContentHash
                                              ?? stepResult.ContentHash
                                              ?? string.Empty;
                        state.HdReady = false;
                    }

                    InstallStateIO.Save(statePath, state);
                }

                if (stepFailed)
                    break;

                // Tip identity wins after the full chain.
                ApplyTrackSuccess(state, new TrackInstallResult
                {
                    Preset = track.Preset,
                    Success = true,
                    CatalogVersion = tip.CatalogVersion,
                    ContentHash = tip.ContentHash,
                });
                ClearLastCompleted(state, track.Preset);

                if (IsClient(track.Preset))
                    completedClient = true;
                if (IsServer(track.Preset))
                    completedServer = true;
                if (IsPlatform(track.Preset))
                    completedPlatform = true;

                InstallStateIO.Save(statePath, state);
            }

            if (failError is null)
            {
                state.Incomplete = false;
                state.LastError = null;
                if (mode == InstallMode.Full)
                {
                    var wantedClient = plan.Tracks.Any(t => IsClient(t.Preset));
                    var wantedServer = plan.Tracks.Any(t => IsServer(t.Preset));
                    if (wantedClient && !completedClient)
                    {
                        state.Incomplete = true;
                        state.LastError = "Full install missing client track success.";
                    }
                    if (wantedServer && !completedServer)
                    {
                        state.Incomplete = true;
                        state.LastError = "Full install missing server track success.";
                    }
                    var wantedPlatform = plan.Tracks.Any(t => IsPlatform(t.Preset));
                    if (wantedPlatform && !completedPlatform)
                    {
                        state.Incomplete = true;
                        state.LastError = "Full install missing platform track success.";
                    }
                }

                // Persist success first so assessor sees ready flags + incomplete=false.
                if (!state.Incomplete)
                {
                    InstallStateIO.Save(statePath, state);

                    var needClient = mode is InstallMode.Full or InstallMode.ClientOnly;
                    var needServer = mode is InstallMode.Full
                        or InstallMode.ServerSecondary
                        or InstallMode.DedicatedOnly;

                    var health = Assess(channel, installPath, needClient, needServer);
                    if (health.NeedsRepair ||
                        health.Overall is InstallHealthStatus.Incomplete
                            or InstallHealthStatus.Missing)
                    {
                        state.Incomplete = true;
                        state.LastError = "Post-install health failed: " + health.Summary;
                        if (health.Client?.Status == InstallHealthStatus.Corrupted)
                            state.ClientReady = false;
                        if (health.Server?.Status == InstallHealthStatus.Corrupted)
                            state.ServerReady = false;
                    }
                }
            }
            else
            {
                state.Incomplete = true;
                state.LastError = failError;
                if (preserveClientReady)
                {
                    state.ClientReady = priorClientReady || state.ClientReady;
                    if (priorClientReady)
                    {
                        state.ClientCatalogVersion ??= priorClientCatalog;
                        state.ClientContentHash ??= priorClientHash;
                    }
                }
            }

            InstallStateIO.Save(statePath, state);
            progress?.Report(new ContentInstallProgress
            {
                Phase = state.Incomplete ? "failed" : "complete",
                Current = plan.Tracks.Count,
                Total = plan.Tracks.Count,
                Message = state.Incomplete
                    ? (state.LastError ?? "incomplete")
                    : "install complete",
            });

            return state;
        }
        catch (OperationCanceledException)
        {
            state.Incomplete = true;
            state.LastError = "cancelled";
            InstallStateIO.Save(statePath, state);
            throw;
        }
        finally
        {
            if (ownsFetcher && fetcher is IDisposable d)
                d.Dispose();
        }
    }

    static async Task<TrackInstallResult> InstallBaseStepAsync(
        string preset,
        UpdateStep step,
        string installPath,
        string cacheRoot,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel,
        OverlayExtractPolicy overlay)
    {
        var trackPlan = new TrackPlan
        {
            Preset = preset,
            CatalogVersion = step.ToVersion,
            ContentHash = step.ContentHash ?? string.Empty,
            ShareManifestUrl = step.ShareManifestUrl,
            Assets = step.Assets,
            TotalBytes = step.TotalBytes,
        };

        var result = await TrackInstaller.InstallTrackAsync(
            trackPlan,
            installPath,
            cacheRoot,
            fetcher,
            progress,
            cancel,
            overlay).ConfigureAwait(false);

        if (result.Success)
        {
            if (!string.IsNullOrWhiteSpace(step.ContentHash))
                result.ContentHash = step.ContentHash;
            result.CatalogVersion = step.ToVersion;
        }

        return result;
    }

    /// <summary>The tip a track plan came from, for callers outside this file.</summary>
    public static ChannelTrackTip? TipFor(ChannelManifest channel, string preset) =>
        GetTip(channel, preset);

    static ChannelTrackTip? GetTip(ChannelManifest channel, string preset)
    {
        if (IsClient(preset))
            return channel.Client;
        if (IsServer(preset))
            return channel.Server;
        if (IsPlatform(preset))
            return channel.Platform;
        if (IsHd(preset))
            return channel.Hd;
        return null;
    }

    static void SetLastCompleted(InstallState state, string preset, string stepId)
    {
        if (IsClient(preset))
            state.ClientLastCompletedStep = stepId;
        else if (IsServer(preset))
            state.ServerLastCompletedStep = stepId;
        else if (IsPlatform(preset))
            state.PlatformLastCompletedStep = stepId;
        else if (IsHd(preset))
            state.HdLastCompletedStep = stepId;
    }

    static void ClearLastCompleted(InstallState state, string preset)
    {
        if (IsClient(preset))
            state.ClientLastCompletedStep = null;
        else if (IsServer(preset))
            state.ServerLastCompletedStep = null;
        else if (IsPlatform(preset))
            state.PlatformLastCompletedStep = null;
        else if (IsHd(preset))
            state.HdLastCompletedStep = null;
    }

    static void TryPlatformLeftoverWipe(string installPath, string preset, string version)
    {
        var manPath = InstallHealthAssessor.FindCachedShareManifest(installPath, preset, version);
        if (manPath is null || !File.Exists(manPath))
            return;
        var man = ShareManifestIO.Load(manPath);
        OverlayLeftoverWipe.Apply(installPath, man);
        var dest = OverlayLeftoverWipe.LastSharePath(installPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(manPath, dest, overwrite: true);
    }

    static InstallState LoadOrCreateState(string statePath, string installPath, string sdkVersion)
    {
        if (File.Exists(statePath))
        {
            try
            {
                return InstallStateIO.Load(statePath);
            }
            catch
            {
                // Corrupt marker: start fresh.
            }
        }

        return new InstallState
        {
            Schema = 1,
            InstallPath = installPath,
            SdkVersionExpected = sdkVersion,
            Incomplete = true,
        };
    }

    static void ApplyTrackSuccess(InstallState state, TrackInstallResult result)
    {
        if (IsClient(result.Preset))
        {
            state.ClientReady = true;
            state.ClientCatalogVersion = result.CatalogVersion;
            state.ClientContentHash = result.ContentHash;
        }
        else if (IsServer(result.Preset))
        {
            state.ServerReady = true;
            state.ServerCatalogVersion = result.CatalogVersion;
            state.ServerContentHash = result.ContentHash;
        }
        else if (IsPlatform(result.Preset))
        {
            state.PlatformReady = true;
            state.PlatformCatalogVersion = result.CatalogVersion;
            state.PlatformContentHash = result.ContentHash;
        }
        else if (IsHd(result.Preset))
        {
            state.HdReady = true;
            state.HdCatalogVersion = result.CatalogVersion;
            state.HdContentHash = result.ContentHash;
        }
    }

    static bool IsClient(string preset) =>
        string.Equals(preset, "client", StringComparison.OrdinalIgnoreCase);

    static bool IsServer(string preset) =>
        string.Equals(preset, "server", StringComparison.OrdinalIgnoreCase);

    static bool IsPlatform(string preset) =>
        string.Equals(preset, "platform", StringComparison.OrdinalIgnoreCase);

    static string? InstalledHashFor(InstallState state, string preset)
    {
        if (IsClient(preset)) return state.ClientContentHash;
        if (IsServer(preset)) return state.ServerContentHash;
        if (IsPlatform(preset)) return state.PlatformContentHash;
        if (IsHd(preset)) return state.HdContentHash;
        return null;
    }

    static bool IsHd(string preset) =>
        string.Equals(preset, "hd", StringComparison.OrdinalIgnoreCase);

    static InstallState? TryLoadState(string installPath)
    {
        try
        {
            var p = Path.Combine(installPath, ProductConstants.InstallStateFileName);
            return File.Exists(p) ? InstallStateIO.Load(p) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What enabling HD will cost: bytes still to fetch, and whether the drive
    /// holds them. Payload only -- a content install writes files in place and
    /// needs no unpack scratch.
    /// </summary>
    public static HdSpaceReport PreflightHd(ChannelManifest channel, string installPath)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var report = new HdSpaceReport
        {
            FreeBytes = InstallPathPolicy.FreeBytes(installPath),
        };

        if (!InstallPlanner.HasDownloadableTip(channel.Hd))
        {
            report.Available = false;
            return report;
        }

        report.Available = true;
        report.TotalBytes = channel.Hd!.TotalBytes ?? 0;

        var manPath = InstallHealthAssessor.FindCachedShareManifest(
            installPath, "hd", channel.Hd.CatalogVersion);
        if (manPath is not null && File.Exists(manPath))
        {
            try
            {
                var man = ContentManifestIO.Load(manPath);
                report.TotalBytes = HdTextureSet.RequiredBytes(man);
                report.MissingBytes = HdTextureSet.MissingBytes(installPath, man);
                return report;
            }
            catch
            {
                // No cached manifest yet -- the tip total is the honest quote.
            }
        }

        report.MissingBytes = report.TotalBytes;
        return report;
    }

    /// <summary>
    /// Record the player's HD choice. Turning it off deletes the textures --
    /// the client probes the core packs on disk, so a toggle that only writes
    /// a flag would leave HD on. Turning it on only records intent; the caller
    /// runs an install to fetch.
    /// </summary>
    public static HdToggleResult SetHdEnabled(string installPath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var statePath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
        if (!File.Exists(statePath))
        {
            // A master tree has no INSTALL_STATE. Creating one marks the folder
            // Incomplete and the shell offers a full pack download over it.
            return new HdToggleResult { Enabled = enabled };
        }

        var state = InstallStateIO.Load(statePath);

        var result = new HdToggleResult { Enabled = enabled };

        if (!enabled)
        {
            var (deleted, bytes, failed) = HdTextureSet.RemoveAll(installPath);
            result.FilesRemoved = deleted;
            result.BytesReclaimed = bytes;
            result.Failed = failed;
            state.HdReady = false;
            state.HdCatalogVersion = null;
            state.HdContentHash = null;
            state.HdLastCompletedStep = null;
        }

        state.HdEnabled = enabled;
        InstallStateIO.Save(statePath, state);
        return result;
    }
}

public sealed class HdSpaceReport
{
    /// <summary>False when the channel tip carries no HD track.</summary>
    public bool Available { get; set; }
    public long TotalBytes { get; set; }
    public long MissingBytes { get; set; }
    public long FreeBytes { get; set; }

    public bool FitsOnDisk => FreeBytes < 0 || FreeBytes >= MissingBytes;

    /// <summary>Windows reports GiB, so quote GiB and say GiB.</summary>
    public double MissingGiB => MissingBytes / 1024.0 / 1024.0 / 1024.0;

    public double TotalGiB => TotalBytes / 1024.0 / 1024.0 / 1024.0;

    public double FreeGiB => FreeBytes < 0 ? 0 : FreeBytes / 1024.0 / 1024.0 / 1024.0;
}

public sealed class HdToggleResult
{
    public bool Enabled { get; set; }
    public int FilesRemoved { get; set; }
    public long BytesReclaimed { get; set; }
    public List<string> Failed { get; set; } = new();
}
