using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Compare CHANNEL tips + INSTALL_STATE + optional SHARE file lists under InstallPath.
/// Local tips with empty assets and local_install_hint are not enforced (master tree play).
/// </summary>
public static class InstallHealthAssessor
{
    public static InstallHealthReport Assess(
        ChannelManifest? channel,
        string installPath,
        bool requireClient = true,
        bool requireServer = false)
    {
        var report = new InstallHealthReport
        {
            InstallPath = installPath ?? string.Empty,
        };

        if (string.IsNullOrWhiteSpace(installPath))
        {
            report.Overall = InstallHealthStatus.Missing;
            report.Enforced = true;
            report.Reasons.Add("Install path empty.");
            return report;
        }

        // Mixed client SHARE carries dedi. No separate server tip = not a missing track.
        if (requireServer && !TrackHasAssets(channel?.Server))
            requireServer = false;

        var statePath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
        InstallState? state = null;
        if (File.Exists(statePath))
        {
            try
            {
                state = InstallStateIO.Load(statePath);
                report.HasInstallState = true;
            }
            catch (Exception ex)
            {
                report.HasInstallState = true;
                report.Overall = InstallHealthStatus.Corrupted;
                report.Enforced = true;
                report.Reasons.Add("INSTALL_STATE unreadable: " + ex.Message);
                return report;
            }
        }

        // No channel + no state → not enforced (local master preflight only).
        if (channel is null && state is null)
        {
            return MasterTreeReady(report, installPath, requireClient, requireServer,
                "No CHANNEL / INSTALL_STATE — preflight only.");
        }

        // Channel present but only local hints (no download assets): soft-ready.
        var channelHasDownloadableAssets =
            channel is not null &&
            (TrackHasAssets(channel.Client) ||
             TrackHasAssets(channel.Server) ||
             TrackHasAssets(channel.Platform));

        if (channel is not null && !channelHasDownloadableAssets && state is null)
        {
            return MasterTreeReady(report, installPath, requireClient, requireServer,
                "Local CHANNEL tip (no share assets) — preflight only.");
        }

        // Master tree (s21-full etc.): triad present, no pack INSTALL_STATE.
        // Do not report Missing just because a pack CHANNEL is loaded for smoke/tools.
        if (state is null && IsMasterTree(installPath, requireClient, requireServer, channel))
        {
            return MasterTreeReady(report, installPath, requireClient, requireServer,
                "Master install tree (no INSTALL_STATE) — pack not required.");
        }

        report.Enforced = channelHasDownloadableAssets || state is not null;

        if (requireClient || channel?.Client is not null)
        {
            report.Client = AssessTrack(
                "client",
                channel?.Client,
                state,
                installPath,
                required: requireClient,
                readyFlag: state?.ClientReady ?? false,
                installedVer: state?.ClientCatalogVersion,
                installedHash: state?.ClientContentHash,
                incomplete: state?.Incomplete ?? true);
        }

        if (requireServer || channel?.Server is not null)
        {
            report.Server = AssessTrack(
                "server",
                channel?.Server,
                state,
                installPath,
                required: requireServer,
                readyFlag: state?.ServerReady ?? false,
                installedVer: state?.ServerCatalogVersion,
                installedHash: state?.ServerContentHash,
                incomplete: state?.Incomplete ?? true);
        }

        if (channel?.Platform is not null)
        {
            report.Platform = AssessTrack(
                "platform",
                channel.Platform,
                state,
                installPath,
                required: TrackHasAssets(channel.Platform),
                readyFlag: state?.PlatformReady ?? false,
                installedVer: state?.PlatformCatalogVersion,
                installedHash: state?.PlatformContentHash,
                incomplete: state?.Incomplete ?? true);
        }

        // HD is opt-in. A player who never enabled it must never see the install
        // go unhealthy because the tip carries textures they did not ask for.
        var hdEnabled = state?.HdEnabled ?? false;
        if (channel?.Hd is not null && hdEnabled)
        {
            report.Hd = AssessTrack(
                "hd",
                channel.Hd,
                state,
                installPath,
                required: true,
                readyFlag: state?.HdReady ?? false,
                installedVer: state?.HdCatalogVersion,
                installedHash: state?.HdContentHash,
                incomplete: state?.Incomplete ?? true);
        }

        var statuses = new List<InstallHealthStatus>();
        if (report.Hd is not null && hdEnabled)
            statuses.Add(report.Hd.Status);
        if (report.Client is not null && requireClient)
            statuses.Add(report.Client.Status);
        if (report.Server is not null && requireServer)
            statuses.Add(report.Server.Status);
        if (report.Platform is not null && TrackHasAssets(channel?.Platform))
            statuses.Add(report.Platform.Status);

        // If nothing required, still surface worst present track for UI.
        if (statuses.Count == 0)
        {
            if (report.Client is not null)
                statuses.Add(report.Client.Status);
            if (report.Server is not null)
                statuses.Add(report.Server.Status);
        }

        report.Overall = Worst(statuses);
        CollectReasons(report);
        return report;
    }

    static TrackHealth AssessTrack(
        string preset,
        ChannelTrackTip? tip,
        InstallState? state,
        string installPath,
        bool required,
        bool readyFlag,
        string? installedVer,
        string? installedHash,
        bool incomplete)
    {
        var h = new TrackHealth
        {
            Preset = preset,
            InstalledCatalogVersion = installedVer,
            InstalledContentHash = installedHash,
            TipCatalogVersion = tip?.CatalogVersion,
            TipContentHash = tip?.ContentHash,
        };

        if (tip is null)
        {
            if (readyFlag)
            {
                h.Status = InstallHealthStatus.Ready;
                h.Reason = "Installed; no tip on channel.";
                return h;
            }

            h.Status = InstallHealthStatus.Missing;
            h.Reason = required
                ? $"CHANNEL has no {preset} tip."
                : $"No {preset} tip and not installed.";
            return h;
        }

        var tipDownloadable = TrackHasAssets(tip);

        if (state is null || !readyFlag)
        {
            if (!tipDownloadable && !string.IsNullOrWhiteSpace(tip.LocalInstallHint))
            {
                h.Status = InstallHealthStatus.Ready;
                h.Reason = "Local install hint (no pack assets).";
                return h;
            }

            // A platform tip added after this install completed is a small
            // update, not a broken install — do not bounce to the setup screen.
            if (state is not null &&
                !incomplete &&
                string.Equals(preset, "platform", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(installedVer))
            {
                h.Status = InstallHealthStatus.UpdateAvailable;
                h.Reason = $"platform pack not installed; channel tip {tip.CatalogVersion}";
                return h;
            }

            h.Status = state is null
                ? InstallHealthStatus.Missing
                : InstallHealthStatus.Incomplete;
            h.Reason = readyFlag
                ? "Incomplete install."
                : $"{preset} not ready.";
            if (incomplete && state is not null)
                h.Reason = state.LastError ?? "incomplete=true";
            return h;
        }

        // Ready flag set: check tip drift.
        if (!string.IsNullOrWhiteSpace(tip.CatalogVersion) &&
            !string.IsNullOrWhiteSpace(installedVer) &&
            !string.Equals(tip.CatalogVersion, installedVer, StringComparison.Ordinal))
        {
            h.Status = InstallHealthStatus.UpdateAvailable;
            h.Reason =
                $"catalog {installedVer} installed; channel tip {tip.CatalogVersion}";
            return h;
        }

        if (!string.IsNullOrWhiteSpace(tip.ContentHash) &&
            !string.IsNullOrWhiteSpace(installedHash) &&
            !string.Equals(tip.ContentHash, installedHash, StringComparison.OrdinalIgnoreCase))
        {
            h.Status = InstallHealthStatus.UpdateAvailable;
            h.Reason = "content_hash differs from CHANNEL tip.";
            return h;
        }

        // File verify when we have a cached SHARE_MANIFEST.
        var manPath = FindCachedShareManifest(installPath, preset, installedVer ?? tip.CatalogVersion);
        if (manPath is not null && File.Exists(manPath))
        {
            try
            {
                var man = ShareManifestIO.Load(manPath);
                var isPlatform = string.Equals(preset, "platform", StringComparison.OrdinalIgnoreCase);

                // The platform pack owns the overlay files, so it still checks they
                // exist -- but a size change there means the player edited a script,
                // playlist or cfg, which the KeepEdits install policy invites. Only
                // absence is breakage.
                var vr = ShareFileVerifier.VerifyInstallFiles(
                    installPath,
                    man,
                    skipRelativePath: isPlatform ? null : OverlayPaths.SkipFatVerify,
                    sizeOptionalRelativePath: isPlatform ? OverlayPaths.SkipFatVerify : null);
                h.MissingFileCount = vr.Missing;
                h.SizeMismatchCount = vr.SizeMismatch;
                h.SampleIssues = vr.Samples;
                if (!vr.Ok)
                {
                    h.Status = InstallHealthStatus.Corrupted;
                    h.Reason =
                        $"files broken (missing={vr.Missing} size_mismatch={vr.SizeMismatch})";
                    return h;
                }
            }
            catch (Exception ex)
            {
                h.Status = InstallHealthStatus.Corrupted;
                h.Reason = "SHARE_MANIFEST verify failed: " + ex.Message;
                return h;
            }
        }
        else
        {
            var contentManPath = ContentManifestCachePath(
                installPath, preset, installedVer ?? tip.CatalogVersion);
            var wantContentMan = tip.UsesContentManifest
                                 || (contentManPath is not null && File.Exists(contentManPath));
            if (wantContentMan)
            {
                if (contentManPath is null || !File.Exists(contentManPath))
                {
                    if (tipDownloadable && tip.UsesContentManifest && readyFlag)
                    {
                        h.Status = InstallHealthStatus.Corrupted;
                        h.Reason = "content manifest missing";
                        return h;
                    }
                }
                else
                {
                    try
                    {
                        var cman = ContentManifestIO.Load(contentManPath);
                        var missing = 0;
                        var sizeMismatch = 0;
                        var samples = new List<string>();
                        foreach (var f in cman.Files)
                        {
                            if (OverlayPaths.IsOverlayOwned(f.Path) ||
                                OverlayPaths.IsOverlayOptional(f.Path))
                                continue;
                            if (!SafePath.TryJoin(installPath, f.Path, out var full))
                            {
                                missing++;
                                if (samples.Count < 4)
                                    samples.Add(f.Path + " (bad path)");
                                continue;
                            }
                            if (!File.Exists(full))
                            {
                                missing++;
                                if (samples.Count < 4)
                                    samples.Add(f.Path);
                                continue;
                            }
                            var len = new FileInfo(full).Length;
                            if (len != f.Size)
                            {
                                sizeMismatch++;
                                if (samples.Count < 4)
                                    samples.Add($"{f.Path} size {len} expected {f.Size}");
                            }
                        }

                        h.MissingFileCount = missing;
                        h.SizeMismatchCount = sizeMismatch;
                        h.SampleIssues = samples;
                        if (missing > 0 || sizeMismatch > 0)
                        {
                            h.Status = InstallHealthStatus.Corrupted;
                            h.Reason =
                                $"files broken (missing={missing} size_mismatch={sizeMismatch})";
                            return h;
                        }
                    }
                    catch (Exception ex)
                    {
                        h.Status = InstallHealthStatus.Corrupted;
                        h.Reason = "CONTENT_MANIFEST verify failed: " + ex.Message;
                        return h;
                    }
                }

                if (!LightTriadOk(installPath, preset))
                {
                    h.Status = InstallHealthStatus.Corrupted;
                    h.Reason = $"{preset} triad/marker missing.";
                    return h;
                }
            }
            else if (tipDownloadable)
            {
                if (!LightTriadOk(installPath, preset))
                {
                    h.Status = InstallHealthStatus.Corrupted;
                    h.Reason = $"{preset} triad/marker missing (no cache to full-verify).";
                    return h;
                }
            }
        }

        if (state is not null && incomplete && required)
        {
            h.Status = InstallHealthStatus.Incomplete;
            h.Reason = state.LastError ?? "incomplete=true";
            return h;
        }

        h.Status = InstallHealthStatus.Ready;
        h.Reason = "ok";
        return h;
    }

    static InstallHealthReport MasterTreeReady(
        InstallHealthReport report,
        string installPath,
        bool requireClient,
        bool requireServer,
        string reason)
    {
        report.Overall = InstallHealthStatus.Ready;
        report.Enforced = false;
        report.Reasons.Add(reason);

        var clientOk = LightTriadOk(installPath, "client");
        var serverOk = LightTriadOk(installPath, "server");
        report.Client = new TrackHealth
        {
            Preset = "client",
            Status = clientOk ? InstallHealthStatus.Ready : InstallHealthStatus.Missing,
            Reason = clientOk ? "master triad" : "client triad missing",
        };
        report.Server = new TrackHealth
        {
            Preset = "server",
            Status = serverOk ? InstallHealthStatus.Ready : InstallHealthStatus.Missing,
            Reason = serverOk ? "master triad" : "server triad missing",
        };

        // Surface missing triad in overall when required.
        var statuses = new List<InstallHealthStatus>();
        if (requireClient && report.Client is not null)
            statuses.Add(report.Client.Status);
        if (requireServer && report.Server is not null)
            statuses.Add(report.Server.Status);
        if (statuses.Count == 0)
        {
            if (report.Client is not null)
                statuses.Add(report.Client.Status);
            if (report.Server is not null)
                statuses.Add(report.Server.Status);
        }

        report.Overall = Worst(statuses);
        if (report.Overall != InstallHealthStatus.Ready)
            report.Enforced = true;
        CollectReasons(report);
        return report;
    }

    /// <summary>
    /// True when InstallPath looks like an operator master tree (s21-full):
    /// triad files present and no pack INSTALL_STATE was written.
    /// </summary>
    static bool IsMasterTree(
        string installPath,
        bool requireClient,
        bool requireServer,
        ChannelManifest? channel)
    {
        if (requireClient && !LightTriadOk(installPath, "client"))
            return false;
        if (requireServer && !LightTriadOk(installPath, "server"))
            return false;

        // When neither flag set (UI overall), accept either triad.
        if (!requireClient && !requireServer)
        {
            if (!LightTriadOk(installPath, "client") && !LightTriadOk(installPath, "server"))
                return false;
        }

        // A named hint that does not match this folder is a pack install.
        // No hint at all: triad + no INSTALL_STATE (caller) is the master tree,
        // including when the live pack CHANNEL is loaded for notes/tools.
        if (channel is not null)
        {
            var sawHint = false;
            foreach (var tip in new[] { channel.Client, channel.Server })
            {
                var hint = tip?.LocalInstallHint;
                if (string.IsNullOrWhiteSpace(hint))
                    continue;
                sawHint = true;
                try
                {
                    var a = Path.GetFullPath(hint.Trim());
                    var b = Path.GetFullPath(installPath.Trim());
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // ignore bad paths
                }
            }
            if (sawHint)
                return false;
        }

        return true;
    }

    public static string? FindCachedShareManifest(
        string installPath,
        string preset,
        string? catalogVersion)
    {
        var cacheRoot = Path.Combine(
            installPath,
            ProductConstants.ContentCacheDirName,
            "cache");
        if (!Directory.Exists(cacheRoot))
            return null;

        if (!string.IsNullOrWhiteSpace(catalogVersion))
        {
            var exact = Path.Combine(
                cacheRoot,
                Sanitize($"{preset}_{catalogVersion}"),
                ProductConstants.ShareManifestFileName);
            if (File.Exists(exact))
                return exact;
        }

        // Fallback: any cache dir starting with preset_
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(cacheRoot, preset + "_*"))
            {
                var cand = Path.Combine(dir, ProductConstants.ShareManifestFileName);
                if (File.Exists(cand))
                    return cand;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    static bool LightTriadOk(string installPath, string preset)
    {
        if (string.Equals(preset, "client", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(installPath, "client_marker.txt")))
                return true;
            // s21-full: r5apex.exe + loader.dll + client.dll at root (or game\).
            return FileExistsAny(installPath, "r5apex.exe")
                   && FileExistsAny(installPath, "client.dll")
                   && FileExistsAny(installPath, "loader.dll");
        }

        if (string.Equals(preset, "server", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(installPath, "server_marker.txt")))
                return true;
            // s21-full: r5apex_ds.exe + server.dll; loader shared with client.
            return FileExistsAny(installPath, "r5apex_ds.exe")
                   && FileExistsAny(installPath, "server.dll")
                   && FileExistsAny(installPath, "loader.dll");
        }

        return true;
    }

    static bool FileExistsAny(string root, string name)
    {
        if (File.Exists(Path.Combine(root, name)))
            return true;
        if (File.Exists(Path.Combine(root, "game", name)))
            return true;
        return false;
    }

    static bool TrackHasAssets(ChannelTrackTip? tip)
    {
        if (tip is null)
            return false;
        if (tip.UsesContentManifest)
            return true;
        if (!string.IsNullOrWhiteSpace(tip.ShareManifestUrl))
            return true;
        if (HasDownloadableAssets(tip.Assets))
            return true;
        if (tip.Base is not null)
        {
            if (!string.IsNullOrWhiteSpace(tip.Base.ShareManifestUrl))
                return true;
            if (HasDownloadableAssets(tip.Base.Assets))
                return true;
        }
        if (tip.Patches is { Count: > 0 })
        {
            foreach (var p in tip.Patches)
            {
                if (!string.IsNullOrWhiteSpace(p.ShareManifestUrl) ||
                    !string.IsNullOrWhiteSpace(p.PatchManifestUrl) ||
                    HasDownloadableAssets(p.Assets))
                {
                    return true;
                }
            }
        }
        return false;
    }

    static bool HasDownloadableAssets(List<ChannelAssetRef>? assets)
    {
        if (assets is null || assets.Count == 0)
            return false;
        return assets.Any(a =>
            string.Equals(a.Kind, "share_manifest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Kind, "share_volume", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Kind, "7z_volume", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Kind, "patch_manifest", StringComparison.OrdinalIgnoreCase));
    }

    static InstallHealthStatus Worst(List<InstallHealthStatus> list)
    {
        if (list.Count == 0)
            return InstallHealthStatus.Ready;

        // Priority: Corrupted > UpdateAvailable > Incomplete > Missing > Ready
        if (list.Contains(InstallHealthStatus.Corrupted))
            return InstallHealthStatus.Corrupted;
        if (list.Contains(InstallHealthStatus.UpdateAvailable))
            return InstallHealthStatus.UpdateAvailable;
        if (list.Contains(InstallHealthStatus.Incomplete))
            return InstallHealthStatus.Incomplete;
        if (list.Contains(InstallHealthStatus.Missing))
            return InstallHealthStatus.Missing;
        return InstallHealthStatus.Ready;
    }

    static void CollectReasons(InstallHealthReport report)
    {
        void Add(TrackHealth? t)
        {
            if (t is null || t.Status == InstallHealthStatus.Ready)
                return;
            if (!string.IsNullOrWhiteSpace(t.Reason))
                report.Reasons.Add($"{t.Preset}: {t.Reason}");
        }

        Add(report.Client);
        Add(report.Server);
        Add(report.Platform);
        if (report.Reasons.Count == 0 && report.Overall != InstallHealthStatus.Ready)
            report.Reasons.Add(report.Overall.ToString());
    }

    static string? ContentManifestCachePath(
        string installPath,
        string preset,
        string? catalogVersion)
    {
        if (string.IsNullOrWhiteSpace(catalogVersion))
            return null;
        return Path.Combine(
            installPath,
            ProductConstants.ContentCacheDirName,
            "manifests",
            Sanitize($"{preset}_{catalogVersion}.json"));
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
