namespace R5Flowstate.Contracts;

/// <summary>Pure planning result for a content install into a shared InstallPath.</summary>
public sealed class InstallPlan
{
    public InstallMode Mode { get; set; }
    public string InstallPath { get; set; } = string.Empty;
    public string CacheRoot { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public List<TrackPlan> Tracks { get; set; } = new();
}

public sealed class TrackPlan
{
    /// <summary>client | server</summary>
    public string Preset { get; set; } = string.Empty;
    public string CatalogVersion { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string ShareManifestUrl { get; set; } = string.Empty;
    public List<ChannelAssetRef> Assets { get; set; } = new();
    public long TotalBytes { get; set; }
}

public static class InstallPlanner
{
    /// <param name="includeHd">
    /// The player's HD opt-in. HD is client-side only, so a dedicated-only
    /// install never carries it however the flag is set.
    /// </param>
    public static InstallPlan Build(
        ChannelManifest channel,
        InstallMode mode,
        string installPath,
        bool includeHd = false)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        var plan = new InstallPlan
        {
            Mode = mode,
            InstallPath = installPath,
            CacheRoot = Path.Combine(installPath, ProductConstants.ContentCacheDirName, "cache"),
            SdkVersion = channel.EffectiveGateName,
        };

        switch (mode)
        {
            case InstallMode.Full:
                if (channel.Client is null)
                    throw new InvalidOperationException(
                        "InstallMode.Full requires channel.client tip.");
                plan.Tracks.Add(BuildTrack(channel, channel.Client, "client"));
                if (channel.Server is not null)
                    plan.Tracks.Add(BuildTrack(channel, channel.Server, "server"));
                if (HasDownloadableTip(channel.Platform))
                    plan.Tracks.Add(BuildTrack(channel, channel.Platform!, "platform"));
                if (includeHd && HasDownloadableTip(channel.Hd))
                    plan.Tracks.Add(BuildTrack(channel, channel.Hd!, "hd"));
                break;

            case InstallMode.ClientOnly:
                if (channel.Client is null)
                    throw new InvalidOperationException(
                        "InstallMode.ClientOnly requires channel.client tip.");
                plan.Tracks.Add(BuildTrack(channel, channel.Client, "client"));
                if (HasDownloadableTip(channel.Platform))
                    plan.Tracks.Add(BuildTrack(channel, channel.Platform!, "platform"));
                if (includeHd && HasDownloadableTip(channel.Hd))
                    plan.Tracks.Add(BuildTrack(channel, channel.Hd!, "hd"));
                break;

            case InstallMode.ServerSecondary:
            case InstallMode.DedicatedOnly:
                if (channel.Server is null)
                    throw new InvalidOperationException(
                        $"{mode} requires channel.server tip.");
                plan.Tracks.Add(BuildTrack(channel, channel.Server, "server"));
                if (HasDownloadableTip(channel.Platform))
                    plan.Tracks.Add(BuildTrack(channel, channel.Platform!, "platform"));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown InstallMode.");
        }

        return plan;
    }

    public static void FilterDownloadLanes(InstallPlan plan, bool content, bool platform)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!content)
            plan.Tracks.RemoveAll(t =>
                string.Equals(t.Preset, "client", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Preset, "server", StringComparison.OrdinalIgnoreCase));
        if (!platform)
            plan.Tracks.RemoveAll(t =>
                string.Equals(t.Preset, "platform", StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasDownloadableTip(ChannelTrackTip? tip)
    {
        if (tip is null)
            return false;
        if (tip.UsesContentManifest)
            return true;
        if (!string.IsNullOrWhiteSpace(tip.ShareManifestUrl))
            return true;
        if (tip.Base is not null && !string.IsNullOrWhiteSpace(tip.Base.ShareManifestUrl))
            return true;
        if (tip.Assets is { Count: > 0 })
            return true;
        if (tip.Base?.Assets is { Count: > 0 })
            return true;
        return false;
    }

    static TrackPlan BuildTrack(ChannelManifest channel, ChannelTrackTip tip, string expectedPreset)
    {
        var assets = new List<ChannelAssetRef>(tip.Assets.Count);
        foreach (var a in tip.Assets)
        {
            assets.Add(new ChannelAssetRef
            {
                Name = a.Name,
                Url = ResolveUrl(channel.BaseUrl, a.Url),
                Sha256 = a.Sha256,
                Size = a.Size,
                Kind = a.Kind,
            });
        }

        var shareUrl = tip.ShareManifestUrl;
        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            var fromAsset = tip.Assets.FirstOrDefault(a =>
                string.Equals(a.Kind, "share_manifest", StringComparison.OrdinalIgnoreCase));
            shareUrl = fromAsset?.Url;
        }

        shareUrl = ResolveUrl(channel.BaseUrl, shareUrl ?? string.Empty);

        long total = tip.TotalBytes ?? 0;
        if (total <= 0)
        {
            long sum = 0;
            foreach (var a in assets)
            {
                if (a.Size is long s && s > 0)
                    sum += s;
            }
            total = sum;
        }

        return new TrackPlan
        {
            Preset = expectedPreset,
            CatalogVersion = tip.CatalogVersion ?? string.Empty,
            ContentHash = tip.ContentHash ?? string.Empty,
            ShareManifestUrl = shareUrl,
            Assets = assets,
            TotalBytes = total,
        };
    }

    static string ResolveUrl(string? baseUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;

        if (string.IsNullOrWhiteSpace(baseUrl))
            return url;

        var baseTrim = baseUrl.TrimEnd('/');
        var rel = url.TrimStart('/');
        return $"{baseTrim}/{rel}";
    }
}
