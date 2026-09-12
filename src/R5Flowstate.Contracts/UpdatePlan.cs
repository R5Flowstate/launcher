namespace R5Flowstate.Contracts;

/// <summary>One ordered action in a base + patch-chain update.</summary>
public enum UpdateStepKind
{
    /// <summary>Install the track base share (full track content at base catalog version).</summary>
    InstallBase = 0,

    /// <summary>Unpack a patch share, then honour PATCH_MANIFEST.deleted.</summary>
    ApplyPatch = 1,

    /// <summary>Reconcile the whole track against a CONTENT_MANIFEST.</summary>
    SyncFiles = 2,
}

/// <summary>A single base install or patch application step.</summary>
public sealed class UpdateStep
{
    public UpdateStepKind Kind { get; set; }

    /// <summary>Parent catalog version for <see cref="UpdateStepKind.ApplyPatch"/>; null for base.</summary>
    public string? FromVersion { get; set; }

    /// <summary>Catalog version after this step succeeds.</summary>
    public string ToVersion { get; set; } = string.Empty;

    /// <summary>Expected content hash after this step when the wire doc carries one.</summary>
    public string? ContentHash { get; set; }

    public string ShareManifestUrl { get; set; } = string.Empty;

    public string? ShareManifestSha256 { get; set; }

    /// <summary>PATCH_MANIFEST URL; set only for <see cref="UpdateStepKind.ApplyPatch"/>.</summary>
    public string? PatchManifestUrl { get; set; }

    public string? PatchManifestSha256 { get; set; }

    public List<ChannelAssetRef> Assets { get; set; } = new();

    public long TotalBytes { get; set; }

    /// <summary>CONTENT_MANIFEST URL; set only for <see cref="UpdateStepKind.SyncFiles"/>.</summary>
    public string? ContentManifestUrl { get; set; }

    public string? ContentManifestSha256 { get; set; }

    /// <summary>Base for cas/ object URLs.</summary>
    public string? CasBaseUrl { get; set; }

    /// <summary>Stable id for resume: base:VER, patch:FROM-&gt;TO, or files:VER:hash.</summary>
    public string StepId => Kind switch
    {
        UpdateStepKind.InstallBase => $"base:{ToVersion}",
        UpdateStepKind.SyncFiles => $"files:{ToVersion}:{ContentHash ?? ""}",
        _ => $"patch:{FromVersion}->{ToVersion}",
    };
}

/// <summary>Ordered plan of update steps for one track tip.</summary>
public sealed class UpdatePlan
{
    public string Preset { get; set; } = string.Empty;

    public string TipCatalogVersion { get; set; } = string.Empty;

    public string TipContentHash { get; set; } = string.Empty;

    public List<UpdateStep> Steps { get; set; } = new();

    /// <summary>Sum of planned step total_bytes (download cost, not tip payload size).</summary>
    public long PlannedBytes
    {
        get
        {
            long sum = 0;
            foreach (var s in Steps)
            {
                if (s.TotalBytes > 0)
                    sum += s.TotalBytes;
            }
            return sum;
        }
    }
}

/// <summary>
/// Pure planner: base + patch chain → ordered steps. Validates the chain before
/// any caller downloads a byte.
/// </summary>
public static class UpdatePlanner
{
    /// <summary>
    /// Resolve what to install for <paramref name="track"/> given the installed
    /// catalog version and content hash (null/empty when no ready install).
    /// </summary>
    /// <remarks>
    /// Four cases:
    /// 1. No install, hash mismatch, or empty version → [InstallBase] + every patch.
    /// 2. installed == tip (and hash matches when known) → [].
    /// 3. installed is some patch's <c>from</c> (or base) → that patch and every later one.
    /// 4. installed is unknown to the chain → full rebuild, as case 1.
    /// Content-manifest tracks use 1 and 2 only (one SyncFiles step, or none).
    /// </remarks>
    public static UpdatePlan Resolve(
        ChannelTrackTip track,
        string? installedVersion,
        string? installedHash,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        // A content manifest describes the whole track at its tip, so there
        // is no chain to walk. Already-at-tip must still be a no-op or every
        // platform bump re-scans the 40 GB client tree.
        if (track.UsesContentManifest)
        {
            if (IsInstalledAtTip(track, installedVersion, installedHash))
                return EmptyPlan(track);
            return ContentPlan(track, baseUrl);
        }

        // Refuse non-contiguous / tip-mismatch chains before any fetch.
        ValidateChain(track);

        var plan = new UpdatePlan
        {
            Preset = track.Preset ?? string.Empty,
            TipCatalogVersion = track.CatalogVersion ?? string.Empty,
            TipContentHash = track.ContentHash ?? string.Empty,
        };

        var allSteps = BuildFullChain(track, baseUrl);

        // Case 1 / 4 inputs: no version → full chain.
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            plan.Steps.AddRange(allSteps);
            return plan;
        }

        // Hash mismatch against a known version → full rebuild (case 1).
        if (IsHashMismatch(track, installedVersion, installedHash))
        {
            plan.Steps.AddRange(allSteps);
            return plan;
        }

        // Case 2: already at tip.
        if (IsInstalledAtTip(track, installedVersion, installedHash))
            return plan;

        // Case 3: installed is base or some patch's from (contiguous chain position).
        var startIndex = FindPatchStartIndex(track, installedVersion);
        if (startIndex >= 0)
        {
            // allSteps[0] is InstallBase; patches start at index 1.
            // startIndex is the index into track.Patches; step index is startIndex + 1
            // when a base step exists, else startIndex when legacy single-tip (no patches).
            if (allSteps.Count == 0)
                return plan;

            var hasBaseStep = allSteps[0].Kind == UpdateStepKind.InstallBase;
            if (hasBaseStep)
            {
                // Patches-only from startIndex; if installed is base and first patch
                // is at patches[0], take steps from index 1 + startIndex.
                for (var i = 1 + startIndex; i < allSteps.Count; i++)
                    plan.Steps.Add(allSteps[i]);
            }
            else
            {
                for (var i = startIndex; i < allSteps.Count; i++)
                    plan.Steps.Add(allSteps[i]);
            }

            return plan;
        }

        // Case 4: installed is unknown to the chain → full rebuild, as case 1.
        plan.Steps.AddRange(allSteps);
        return plan;
    }

    /// <summary>
    /// True when INSTALL_STATE already names this tip. Volume tracks treat a
    /// missing hash as a match (version is the identity); CAS does not -- an
    /// empty hash is "we have not verified this payload."
    /// </summary>
    public static bool IsInstalledAtTip(
        ChannelTrackTip track,
        string? installedVersion,
        string? installedHash)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (string.IsNullOrWhiteSpace(installedVersion))
            return false;
        if (!string.Equals(installedVersion, track.CatalogVersion, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(track.ContentHash))
            return true;
        if (track.UsesContentManifest && string.IsNullOrWhiteSpace(installedHash))
            return false;
        if (string.IsNullOrWhiteSpace(installedHash))
            return true;
        return string.Equals(installedHash, track.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    static UpdatePlan EmptyPlan(ChannelTrackTip track) => new()
    {
        Preset = track.Preset ?? string.Empty,
        TipCatalogVersion = track.CatalogVersion ?? string.Empty,
        TipContentHash = track.ContentHash ?? string.Empty,
    };

    /// <summary>
    /// One SyncFiles step. Repair with no installed version still lands here.
    /// Already-at-tip is filtered in <see cref="Resolve"/>.
    /// </summary>
    static UpdatePlan ContentPlan(ChannelTrackTip track, string? baseUrl)
    {
        var plan = EmptyPlan(track);

        plan.Steps.Add(new UpdateStep
        {
            Kind = UpdateStepKind.SyncFiles,
            ToVersion = track.CatalogVersion ?? string.Empty,
            ContentHash = track.ContentHash,
            ContentManifestUrl = ResolveUrl(baseUrl, track.ContentManifestUrl!),
            ContentManifestSha256 = track.ContentManifestSha256,
            CasBaseUrl = string.IsNullOrWhiteSpace(track.CasBaseUrl)
                ? (baseUrl ?? string.Empty)
                : track.CasBaseUrl,
            TotalBytes = track.TotalBytes ?? 0,
        });

        return plan;
    }

    /// <summary>
    /// Validates base/patch contiguity and tip == last <c>to</c>.
    /// Legacy single-tip (no base, no patches) is always valid.
    /// </summary>
    public static void ValidateChain(ChannelTrackTip track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var hasBase = track.Base is not null;
        var patches = track.Patches;
        var hasPatches = patches is { Count: > 0 };

        if (!hasBase && !hasPatches)
            return;

        if (!hasBase)
        {
            throw new InvalidOperationException(
                "Track has patches but no base; refuse to install.");
        }

        var baseVer = track.Base!.CatalogVersion ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseVer))
        {
            throw new InvalidOperationException(
                "Track base is missing catalog_version; refuse to install.");
        }

        if (!hasPatches)
        {
            if (!string.Equals(track.CatalogVersion, baseVer, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Track catalog_version is not the base version when there are no patches; refuse to install.");
            }
            return;
        }

        if (!string.Equals(patches![0].From, baseVer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Patch chain is not contiguous with the base; refuse to install.");
        }

        for (var i = 1; i < patches.Count; i++)
        {
            if (!string.Equals(patches[i].From, patches[i - 1].To, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Patch chain is not contiguous; refuse to install.");
            }
        }

        var lastTo = patches[^1].To ?? string.Empty;
        if (!string.Equals(track.CatalogVersion, lastTo, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Track catalog_version is not the last patch to-version; refuse to install.");
        }
    }

    static List<UpdateStep> BuildFullChain(ChannelTrackTip track, string? baseUrl)
    {
        var steps = new List<UpdateStep>();

        if (track.Base is not null)
        {
            steps.Add(BuildBaseStep(track.Base, track.Preset, baseUrl));
            if (track.Patches is { Count: > 0 })
            {
                foreach (var p in track.Patches)
                    steps.Add(BuildPatchStep(p, baseUrl));
            }
            return steps;
        }

        // Legacy single-tip: one InstallBase from tip-level share fields.
        steps.Add(new UpdateStep
        {
            Kind = UpdateStepKind.InstallBase,
            FromVersion = null,
            ToVersion = track.CatalogVersion ?? string.Empty,
            ContentHash = string.IsNullOrWhiteSpace(track.ContentHash) ? null : track.ContentHash,
            ShareManifestUrl = ResolveUrl(baseUrl, track.ShareManifestUrl ?? FindShareManifestUrl(track.Assets)),
            ShareManifestSha256 = track.ShareManifestSha256,
            Assets = CloneAndResolveAssets(track.Assets, baseUrl),
            TotalBytes = EffectiveTotal(track.TotalBytes, track.Assets),
        });
        return steps;
    }

    static UpdateStep BuildBaseStep(ChannelTrackBase bas, string preset, string? baseUrl)
    {
        var assets = CloneAndResolveAssets(bas.Assets, baseUrl);
        var shareUrl = bas.ShareManifestUrl;
        if (string.IsNullOrWhiteSpace(shareUrl))
            shareUrl = FindShareManifestUrl(bas.Assets);

        return new UpdateStep
        {
            Kind = UpdateStepKind.InstallBase,
            FromVersion = null,
            ToVersion = bas.CatalogVersion ?? string.Empty,
            ContentHash = string.IsNullOrWhiteSpace(bas.ContentHash) ? null : bas.ContentHash,
            ShareManifestUrl = ResolveUrl(baseUrl, shareUrl ?? string.Empty),
            ShareManifestSha256 = bas.ShareManifestSha256,
            Assets = assets,
            TotalBytes = EffectiveTotal(bas.TotalBytes, bas.Assets),
        };
    }

    static UpdateStep BuildPatchStep(ChannelPatchRef patch, string? baseUrl)
    {
        var assets = CloneAndResolveAssets(patch.Assets, baseUrl);
        var shareUrl = patch.ShareManifestUrl;
        if (string.IsNullOrWhiteSpace(shareUrl))
            shareUrl = FindShareManifestUrl(patch.Assets);

        var patchUrl = patch.PatchManifestUrl;
        if (string.IsNullOrWhiteSpace(patchUrl))
            patchUrl = FindAssetUrl(patch.Assets, "patch_manifest");

        return new UpdateStep
        {
            Kind = UpdateStepKind.ApplyPatch,
            FromVersion = patch.From ?? string.Empty,
            ToVersion = patch.To ?? string.Empty,
            ContentHash = string.IsNullOrWhiteSpace(patch.ContentHash) ? null : patch.ContentHash,
            ShareManifestUrl = ResolveUrl(baseUrl, shareUrl ?? string.Empty),
            ShareManifestSha256 = patch.ShareManifestSha256,
            PatchManifestUrl = ResolveUrl(baseUrl, patchUrl ?? string.Empty),
            PatchManifestSha256 = patch.PatchManifestSha256,
            Assets = assets,
            TotalBytes = EffectiveTotal(patch.TotalBytes, patch.Assets),
        };
    }

    /// <summary>
    /// Index into track.Patches for the first patch whose From equals installedVersion.
    /// Returns 0 when installed is the base version. Returns -1 when unknown.
    /// </summary>
    static int FindPatchStartIndex(ChannelTrackTip track, string installedVersion)
    {
        if (track.Base is not null &&
            string.Equals(installedVersion, track.Base.CatalogVersion, StringComparison.Ordinal))
        {
            return 0;
        }

        if (track.Patches is null || track.Patches.Count == 0)
            return -1;

        for (var i = 0; i < track.Patches.Count; i++)
        {
            if (string.Equals(track.Patches[i].From, installedVersion, StringComparison.Ordinal))
                return i;
        }

        // Installed at an intermediate "to" that is the next patch's from is already
        // covered above. Installed at last "to" is the tip and handled as case 2.
        return -1;
    }

    static bool IsHashMismatch(ChannelTrackTip track, string installedVersion, string? installedHash)
    {
        if (string.IsNullOrWhiteSpace(installedHash))
            return false;

        string? expected = null;
        if (string.Equals(installedVersion, track.CatalogVersion, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(track.ContentHash))
        {
            expected = track.ContentHash;
        }
        else if (track.Base is not null &&
                 string.Equals(installedVersion, track.Base.CatalogVersion, StringComparison.Ordinal) &&
                 !string.IsNullOrWhiteSpace(track.Base.ContentHash))
        {
            expected = track.Base.ContentHash;
        }
        else if (track.Patches is not null)
        {
            foreach (var p in track.Patches)
            {
                if (string.Equals(installedVersion, p.To, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(p.ContentHash))
                {
                    expected = p.ContentHash;
                    break;
                }
            }
        }

        if (expected is null)
            return false;

        return !string.Equals(installedHash, expected, StringComparison.OrdinalIgnoreCase);
    }

    static List<ChannelAssetRef> CloneAndResolveAssets(List<ChannelAssetRef>? assets, string? baseUrl)
    {
        var list = new List<ChannelAssetRef>();
        if (assets is null)
            return list;

        foreach (var a in assets)
        {
            list.Add(new ChannelAssetRef
            {
                Name = a.Name,
                Url = ResolveUrl(baseUrl, a.Url),
                Sha256 = a.Sha256,
                Size = a.Size,
                Kind = a.Kind,
            });
        }
        return list;
    }

    static string? FindShareManifestUrl(List<ChannelAssetRef>? assets)
    {
        if (assets is null)
            return null;
        foreach (var a in assets)
        {
            if (string.Equals(a.Kind, "share_manifest", StringComparison.OrdinalIgnoreCase))
                return a.Url;
        }
        return null;
    }

    static string? FindAssetUrl(List<ChannelAssetRef>? assets, string kind)
    {
        if (assets is null)
            return null;
        foreach (var a in assets)
        {
            if (string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase))
                return a.Url;
        }
        return null;
    }

    static long EffectiveTotal(long? declared, List<ChannelAssetRef>? assets)
    {
        if (declared is long d && d > 0)
            return d;
        long sum = 0;
        if (assets is null)
            return 0;
        foreach (var a in assets)
        {
            if (a.Size is long s && s > 0)
                sum += s;
        }
        return sum;
    }

    static string ResolveUrl(string? baseUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;

        if (Path.IsPathRooted(url))
            return url;

        if (string.IsNullOrWhiteSpace(baseUrl))
            return url;

        var baseTrim = baseUrl.TrimEnd('/');
        var rel = url.TrimStart('/');
        return $"{baseTrim}/{rel}";
    }
}
