using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Runs a real install from the console, through the same
/// <see cref="ContentInstallService"/> path the Game Files card uses.
///
///     R5Flowstate --install --lane platform
///     R5Flowstate --install --lane all --install-path ~/Games/R5Flowstate
///
/// The lane exists because the tracks are independently installable: platform is
/// the live script/config overlay (tens of MB) and client is the 45 GB game. That
/// makes the platform lane a real end-to-end install -- real CAS objects, real
/// commits -- without committing to the big download.
///
/// It never touches the EA prefix: the install root and
/// <see cref="LinuxSettings.DefaultPrefixPath"/> are checked to be different
/// before anything is written.
/// </summary>
static class InstallRunner
{
    public static async Task<int> RunAsync(string? lane, string? installPath, bool keepLocal)
    {
        Loc.Initialize(null);

        lane = (lane ?? "platform").Trim().ToLowerInvariant();
        if (lane is not ("platform" or "client" or "all"))
        {
            Console.WriteLine($"FAIL  unknown --lane '{lane}' (platform, client or all).");
            return 2;
        }

        var settings = LinuxSettings.Load();
        var channelUrl = string.IsNullOrWhiteSpace(settings.ChannelUrl)
            ? LinuxSettings.DefaultChannelUrl
            : settings.ChannelUrl.Trim();
        var root = string.IsNullOrWhiteSpace(installPath)
            ? (string.IsNullOrWhiteSpace(settings.InstallPath)
                ? LinuxSettings.DefaultInstallPath()
                : settings.InstallPath)
            : installPath;

        if (SamePath(root, LinuxSettings.DefaultPrefixPath))
        {
            Console.WriteLine($"FAIL  refusing to install into the EA prefix ({root}).");
            Console.WriteLine("      That folder is the Wine prefix, not the game.");
            return 2;
        }

        // Full is the mode for every lane; FilterDownloadLanes does the narrowing,
        // exactly as the card's own pre-flight measures it.
        var allowContent = lane is "client" or "all";
        var allowPlatform = lane is "platform" or "all";

        Console.WriteLine($"Lane:     {lane} (content={allowContent} platform={allowPlatform})");
        Console.WriteLine($"Channel:  {channelUrl}");
        Console.WriteLine($"Install:  {root}");
        Console.WriteLine($"KeepLocal:{keepLocal}");
        Console.WriteLine();

        using var fetcher = new FileSystemFetcher();
        ChannelManifest? channel;
        try
        {
            channel = await ChannelSource.LoadAsync(
                channelUrl,
                fetcher,
                Path.Combine(LinuxSettings.ConfigDir, "channel"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  channel: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (channel is null)
        {
            Console.WriteLine("FAIL  the channel returned no manifest.");
            return 1;
        }

        Console.WriteLine($"Channel:  {channel.Channel} · gate {channel.EffectiveGateName}");

        var (line, danger) = MainViewModel.InstallDiskLine(channel, root);
        Console.WriteLine($"Disk:     {line}");
        if (danger)
        {
            Console.WriteLine("FAIL  not enough free space; refusing to start.");
            return 1;
        }

        var before = ContentInstallService.Assess(
            channel, root, requireClient: allowContent, requireServer: false, keepLocal);
        Console.WriteLine($"Before:   {before.Overall} · {MainViewModel.HealthLine(before)}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var progress = new ConsoleProgress();
        InstallState state;
        try
        {
            state = await ContentInstallService.InstallAsync(
                channel,
                InstallMode.Full,
                root,
                fetcher: fetcher,
                progress: progress,
                cancel: cts.Token,
                runControl: null,
                // Same non-interactive default as the card's download button.
                decideOverlay: report => report.HasEdits
                    ? OverlayExtractPolicy.KeepEdits
                    : OverlayExtractPolicy.WriteOfficial,
                allowContent: allowContent,
                allowPlatform: allowPlatform,
                keepLocalFiles: keepLocal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("CANCELLED  the download stopped; run the same command to resume.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"FAIL  {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        progress.Flush();
        Console.WriteLine();
        Console.WriteLine($"State:    clientReady={state.ClientReady} platformReady={state.PlatformReady} " +
                          $"incomplete={state.Incomplete}");
        if (!string.IsNullOrWhiteSpace(state.LastError))
            Console.WriteLine($"Error:    {state.LastError}");

        var after = ContentInstallService.Assess(
            channel, root, requireClient: allowContent, requireServer: false, keepLocal);
        Console.WriteLine($"After:    {after.Overall} · {MainViewModel.HealthLine(after)}");
        Console.WriteLine($"          {after.Summary}");

        var (afterLine, _) = MainViewModel.InstallDiskLine(channel, root);
        Console.WriteLine($"Disk:     {afterLine}");

        // Did the lane's own track actually land? That is the run's result, not
        // the whole-install verdict the state carries.
        var lanesDone = lane switch
        {
            "platform" => state.PlatformReady,
            "client" => state.ClientReady,
            _ => state.ClientReady && (channel.Platform is null || state.PlatformReady),
        };

        // Upstream's post-install check keys off the install MODE, not the lanes
        // that were left enabled, so a lane-scoped run of InstallMode.Full is told
        // it failed for missing the client it was never asked to fetch.
        var scopedComplaint = !allowContent
            && state.LastError is not null
            && state.LastError.StartsWith("Post-install health failed", StringComparison.Ordinal);

        if (!lanesDone)
        {
            Console.WriteLine();
            Console.WriteLine("FAIL  the lane's track did not complete.");
            return 1;
        }

        if (state.LastError is not null && !scopedComplaint)
        {
            Console.WriteLine();
            Console.WriteLine("FAIL  " + state.LastError);
            return 1;
        }

        Console.WriteLine();
        if (scopedComplaint)
        {
            Console.WriteLine("Note:     INSTALL_STATE reads incomplete because this run asked for the");
            Console.WriteLine("          platform lane only -- upstream's post-install check is mode-based");
            Console.WriteLine("          and still expects the client. Running the full install clears it.");
        }
        Console.WriteLine("Install finished.");
        return 0;
    }

    /// <summary>One line per phase, refreshed in place on a TTY.</summary>
    sealed class ConsoleProgress : IProgress<ContentInstallProgress>
    {
        DateTime _last = DateTime.MinValue;
        int _width;

        public void Report(ContentInstallProgress p)
        {
            var now = DateTime.UtcNow;
            var line = Format(p);
            var important = p.Phase is "done" or "failed" or "overlay" or "repair" or "cleanup";
            if (!important && (now - _last).TotalMilliseconds < 500)
                return;
            _last = now;

            if (important)
            {
                Console.WriteLine();
                Console.WriteLine(line);
                _width = 0;
                return;
            }

            Console.Write('\r');
            Console.Write(line.PadRight(Math.Max(_width, line.Length)));
            _width = Math.Max(_width, line.Length);
        }

        public void Flush()
        {
            if (_width > 0)
                Console.WriteLine();
            _width = 0;
        }

        static string Format(ContentInstallProgress p)
        {
            var track = string.IsNullOrWhiteSpace(p.Track) ? "" : p.Track + " ";
            var phase = (p.Phase ?? "").PadRight(12);
            var counts = p.Current > 0 || p.Total > 0 ? $"{p.Current}/{p.Total}" : "";
            var bytes = p.JobTotal > 0
                ? $"{MainViewModel.DownloadSize(p.JobCurrent)} / {MainViewModel.DownloadSize(p.JobTotal)}"
                : "";
            var message = p.Message ?? p.FileName ?? "";
            var text = $"{track}{phase} {counts,12}  {bytes,-24} {message}";
            return text.Length > 160 ? text[..160] : text;
        }
    }

    /// <summary>Path comparison that ignores a trailing separator and relative segments.</summary>
    static bool SamePath(string a, string b)
    {
        try
        {
            var pa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar);
            var pb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(pa, pb, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
