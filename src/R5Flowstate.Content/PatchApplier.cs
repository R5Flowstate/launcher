using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Apply one patch step: unpack delta share volumes via <see cref="TrackInstaller"/>,
/// then delete every path in PATCH_MANIFEST.deleted.
/// </summary>
public static class PatchApplier
{
    /// <summary>
    /// Unpack the patch share into <paramref name="installPath"/>, then honour deletions.
    /// A failed delete fails the whole step (no silent leftover files).
    /// </summary>
    public static async Task<TrackInstallResult> ApplyPatchAsync(
        string preset,
        UpdateStep step,
        string installPath,
        string cacheRoot,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        OverlayExtractPolicy overlay = OverlayExtractPolicy.WriteOfficial)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(fetcher);
        if (step.Kind != UpdateStepKind.ApplyPatch)
            throw new ArgumentException("Step kind must be ApplyPatch.", nameof(step));
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new ArgumentException("Cache root is required.", nameof(cacheRoot));

        var result = new TrackInstallResult
        {
            Preset = preset,
            CatalogVersion = step.ToVersion,
            ContentHash = step.ContentHash ?? string.Empty,
        };

        try
        {
            if (string.IsNullOrWhiteSpace(step.ShareManifestUrl))
            {
                throw new InvalidOperationException(
                    $"Patch {step.FromVersion}->{step.ToVersion} has no share_manifest_url.");
            }

            if (string.IsNullOrWhiteSpace(step.PatchManifestUrl))
            {
                throw new InvalidOperationException(
                    $"Patch {step.FromVersion}->{step.ToVersion} has no patch_manifest_url.");
            }

            progress?.Report(new ContentInstallProgress
            {
                Phase = "patch",
                Track = preset,
                Message = $"apply patch {step.FromVersion}->{step.ToVersion}",
            });

            // Reuse track install: fetch volumes, verify sha256, unpack share.
            var trackPlan = new TrackPlan
            {
                Preset = preset,
                CatalogVersion = step.ToVersion,
                ContentHash = step.ContentHash ?? string.Empty,
                ShareManifestUrl = step.ShareManifestUrl,
                Assets = step.Assets,
                TotalBytes = step.TotalBytes,
            };

            var unpack = await TrackInstaller.InstallTrackAsync(
                trackPlan,
                installPath,
                cacheRoot,
                fetcher,
                progress,
                cancel,
                overlay).ConfigureAwait(false);

            if (!unpack.Success)
            {
                result.Success = false;
                result.Error = unpack.Error ?? "Patch share install failed.";
                result.VolumeRegets = unpack.VolumeRegets;
                result.VerifyPasses = unpack.VerifyPasses;
                return result;
            }

            result.VolumeRegets = unpack.VolumeRegets;
            result.VerifyPasses = unpack.VerifyPasses;

            // Fetch PATCH_MANIFEST (sha256 skip-if-match via fetcher).
            var patchCache = Path.Combine(
                cacheRoot,
                SanitizeDirSegment($"{preset}_patch_{step.FromVersion}_{step.ToVersion}"));
            Directory.CreateDirectory(patchCache);
            var manDest = Path.Combine(patchCache, ProductConstants.PatchManifestFileName);

            progress?.Report(new ContentInstallProgress
            {
                Phase = "patch_manifest",
                Track = preset,
                Message = $"download PATCH_MANIFEST {step.FromVersion}->{step.ToVersion}",
            });

            await fetcher.DownloadAsync(
                step.PatchManifestUrl,
                manDest,
                step.PatchManifestSha256,
                progress,
                cancel).ConfigureAwait(false);

            var pman = PatchManifestIO.Load(manDest);

            if (!string.Equals(pman.From, step.FromVersion, StringComparison.Ordinal) ||
                !string.Equals(pman.To, step.ToVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"PATCH_MANIFEST from/to ({pman.From}->{pman.To}) does not match step " +
                    $"({step.FromVersion}->{step.ToVersion}).");
            }

            progress?.Report(new ContentInstallProgress
            {
                Phase = "delete",
                Track = preset,
                Message = $"apply {pman.Deleted.Count} deletion(s) for patch {step.ToVersion}",
            });

            ApplyDeletions(installPath, pman.Deleted);

            if (!string.IsNullOrWhiteSpace(step.ContentHash))
                result.ContentHash = step.ContentHash;
            else if (!string.IsNullOrWhiteSpace(unpack.ContentHash))
                result.ContentHash = unpack.ContentHash;

            result.CatalogVersion = step.ToVersion;
            result.Success = true;
            progress?.Report(new ContentInstallProgress
            {
                Phase = "done",
                Track = preset,
                Message = $"patch {step.FromVersion}->{step.ToVersion} ok",
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Delete every relative path under <paramref name="installPath"/>.
    /// Missing paths are ignored (idempotent). A failed delete throws.
    /// </summary>
    public static void ApplyDeletions(string installPath, IReadOnlyList<string> deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var rootFull = Path.GetFullPath(installPath);
        var rootNorm = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var relRaw in deleted)
        {
            if (string.IsNullOrWhiteSpace(relRaw))
                continue;

            if (!SafePath.TryJoin(installPath, relRaw, out var full))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete path outside install root: {relRaw}");
            }

            var targetNorm = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(targetNorm, rootNorm, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete install root: {relRaw}");
            }

            if (File.Exists(full))
            {
                try
                {
                    File.Delete(full);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to delete '{relRaw}' for patch: {ex.Message}", ex);
                }
            }
            else if (Directory.Exists(full))
            {
                try
                {
                    Directory.Delete(full, recursive: true);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to delete directory '{relRaw}' for patch: {ex.Message}", ex);
                }
            }
        }
    }

    static string SanitizeDirSegment(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "patch" : s;
    }
}
