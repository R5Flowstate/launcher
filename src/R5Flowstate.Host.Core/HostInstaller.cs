using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Host;

public sealed record HostTrackPlan(string Track, ChannelTrackTip Tip, UpdatePlan Plan)
{
    public bool UpToDate => Plan.Steps.Count == 0;
}

public sealed class HostUpdateCheck
{
    public required ChannelManifest Channel { get; init; }
    public required IReadOnlyList<HostTrackPlan> Tracks { get; init; }
    public bool UpdateAvailable => Tracks.Any(t => !t.UpToDate);
}

/// <summary>
/// Installs the server track then the platform track from the ring's dedicated
/// CHANNEL. Platform goes last so its scripts and DLLs win.
/// </summary>
public static class HostInstaller
{
    public static readonly string[] TrackOrder = { "server", "platform" };

    public static async Task<HostUpdateCheck> CheckAsync(
        string installPath, HostConfig config, IHttpFetcher fetcher, CancellationToken cancel = default)
    {
        var channel = await ChannelSource.LoadAsync(
            config.EffectiveChannelUrl, fetcher, HostConfig.HostDir(installPath), cancel).ConfigureAwait(false);

        if (channel.Client is not null || channel.Hd is not null)
            throw new InvalidOperationException("That CHANNEL is a player document, not a dedicated one.");

        var state = HostState.Load(installPath);
        var tracks = new List<HostTrackPlan>();
        foreach (var name in TrackOrder)
        {
            var tip = name == "server" ? channel.Server : channel.Platform;
            if (tip is null)
            {
                if (name == "server")
                    throw new InvalidOperationException("The dedicated CHANNEL has no server tip.");
                continue;
            }
            if (!tip.UsesContentManifest)
                throw new InvalidOperationException("The " + name + " tip has no content manifest.");

            state.Tracks.TryGetValue(name, out var have);
            var installedAt = have is not null
                && string.Equals(have.ManifestSha256, tip.ContentManifestSha256, StringComparison.OrdinalIgnoreCase);
            var plan = UpdatePlanner.Resolve(
                tip,
                installedAt ? have!.CatalogVersion : null,
                installedAt ? have!.ContentHash : null,
                channel.BaseUrl);
            tracks.Add(new HostTrackPlan(name, tip, plan));
        }
        return new HostUpdateCheck { Channel = channel, Tracks = tracks };
    }

    /// <summary>
    /// Brings the install to the CHANNEL tip. Refuses while a dedi from this
    /// install is running: the platform track rewrites server.dll.
    /// </summary>
    public static async Task<HostUpdateCheck> UpdateAsync(
        string installPath,
        HostConfig config,
        IHttpFetcher fetcher,
        IProgress<ContentInstallProgress>? progress = null,
        bool repair = false,
        CancellationToken cancel = default)
    {
        Directory.CreateDirectory(installPath);
        if (DediProcesses.CountUnder(installPath) > 0)
            throw new InvalidOperationException("A dedicated server from this install is running. Stop it first.");

        using var dirLock = InstallDirectoryLock.TryAcquire(installPath, out var holder)
            ?? throw new InvalidOperationException("Another installer holds this folder: " + holder);

        var check = await CheckAsync(installPath, config, fetcher, cancel).ConfigureAwait(false);
        var state = HostState.Load(installPath);

        // A server step rewrites overlay files with its own copies, so the
        // platform tip must settle again after it.
        var serverChanged = false;
        foreach (var track in check.Tracks)
        {
            var plan = track.Plan;
            if (plan.Steps.Count == 0 && (repair || (track.Track == "platform" && serverChanged)))
                plan = UpdatePlanner.Resolve(track.Tip, null, null, check.Channel.BaseUrl);

            foreach (var step in plan.Steps)
            {
                cancel.ThrowIfCancellationRequested();
                var result = await ContentTrackInstaller.InstallTrackAsync(
                    track.Track,
                    step,
                    installPath,
                    step.CasBaseUrl ?? check.Channel.BaseUrl ?? HostRings.CdnBase,
                    fetcher,
                    progress,
                    cancel,
                    OverlayExtractPolicy.WriteOfficial).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException(track.Track + " update failed: " + result.Error);
            }

            if (plan.Steps.Count > 0 && track.Track == "server")
                serverChanged = true;

            state.Tracks[track.Track] = new HostTrackState
            {
                CatalogVersion = track.Tip.CatalogVersion,
                ContentHash = track.Tip.ContentHash,
                ManifestSha256 = track.Tip.ContentManifestSha256,
            };
            state.Save(installPath);
        }

        state.GateName = check.Channel.EffectiveGateName;
        state.MarketingTag = check.Channel.MarketingTag;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        state.Save(installPath);
        return check;
    }
}
