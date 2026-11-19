using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the master server: the same three calls the browser, the
/// notice gate and the install gate make (POST /spire/hosts, POST /spire/notice,
/// GET /launcher/status), with nothing written anywhere — no setting changes, no
/// accepted notice, no row joined. It answers "what is the master server listing
/// right now?" without opening the launcher.
///
///     R5Flowstate --servers
///     R5Flowstate --servers --install-path ~/Games/R5Flowstate
///
/// The wire version is the interesting part: the master server gates on it, so
/// the probe prints where the one it used came from (the install's stamp, the
/// channel's gate, or the version this build was compiled against).
/// </summary>
static class ServerCheck
{
    public static async Task<int> RunAsync(string? installPath)
    {
        Loc.Initialize(null);

        var settings = LinuxSettings.Load();
        installPath = string.IsNullOrWhiteSpace(installPath)
            ? (string.IsNullOrWhiteSpace(settings.InstallPath)
                ? LinuxSettings.DefaultInstallPath()
                : settings.InstallPath)
            : installPath.Trim();

        var url = MainViewModel.MasterServerUrl;
        Console.WriteLine($"Master:   {url}");
        Console.WriteLine($"Install:  {installPath}");
        Console.WriteLine();

        // The channel supplies the fallback gate, exactly as the browser does --
        // and it is also the only network call that may fail without failing the
        // probe, since a missing channel just means "use the compiled version".
        string? gate = null;
        using (var fetcher = new FileSystemFetcher())
        {
            try
            {
                var channel = await ChannelSource.LoadAsync(
                    string.IsNullOrWhiteSpace(settings.ChannelUrl)
                        ? LinuxSettings.DefaultChannelUrl
                        : settings.ChannelUrl.Trim(),
                    fetcher,
                    Path.Combine(LinuxSettings.ConfigDir, "channel"),
                    CancellationToken.None).ConfigureAwait(false);
                gate = channel?.EffectiveGateName;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN  channel fetch failed ({ex.GetType().Name}); " +
                                  "falling back to the compiled wire version.");
            }
        }

        var stamp = VersionIdentity.TryReadSdkVersionStamp(installPath);
        var wire = VersionIdentity.ResolveExpectedWireVersion(installPath, gate);
        var source = !string.IsNullOrWhiteSpace(stamp)
            ? "the install's sdk-version stamp"
            : !string.IsNullOrWhiteSpace(gate)
                ? "the channel's gate"
                : "the version this build was compiled against";
        Console.WriteLine($"Wire:     {wire}  (from {source})");
        Console.WriteLine();

        var result = await MasterServerClient.ListServersAsync(url, wire, Loc.Code, CancellationToken.None)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            Console.WriteLine($"FAIL  server list: {result.Error}");
            // The notice and the lane flags still say something useful about a
            // master that answered but refused the list.
        }
        else
        {
            Console.WriteLine($"OK    {result.Servers.Count} server(s) listed " +
                              $"(raw {result.RawCount}, dropped {result.Dropped.Count})");
            if (result.UpdateRequired)
                Console.WriteLine("      the master server wants a newer build; the launcher would say: " +
                                  Loc.Get("browser_update_required"));
            Console.WriteLine();

            var players = 0;
            foreach (var s in result.Servers.Take(20))
            {
                players += s.NumPlayers;
                var joinable = ConnectTarget.TryFormat(s.Ip, s.Port, out var target);
                Console.WriteLine($"  {Trim(s.Name, 34),-34} {s.NumPlayers,3}/{s.MaxPlayers,-3} " +
                                  $"{Trim(s.Map, 26),-26} {Trim(s.Playlist, 14),-14} " +
                                  (joinable ? target : $"REFUSED {s.Ip}:{s.Port}") +
                                  (s.HasPassword ? "  [password]" : "") +
                                  (s.RequiredMods.Count > 0 ? $"  [mods x{s.RequiredMods.Count}]" : ""));
            }
            if (result.Servers.Count > 20)
                Console.WriteLine($"  … and {result.Servers.Count - 20} more");
            Console.WriteLine();
            Console.WriteLine($"      rows shown carry {players} player(s) of " +
                              $"{result.Servers.Sum(s => s.NumPlayers)} total");
        }

        Console.WriteLine();
        var eula = await MasterServerClient.GetEulaAsync(url, NoticeLanguages.ResolveUi(settings.UiLanguage, settings.EulaLanguage), CancellationToken.None)
            .ConfigureAwait(false);
        if (eula.Success)
        {
            var firstLine = eula.Contents.Replace("\r", "").Split('\n')
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            Console.WriteLine($"OK    notice v{eula.Version} ({eula.Lang}), {eula.Contents.Length:N0} chars");
            Console.WriteLine($"      starts: {Trim(firstLine.Trim(), 88)}");
            Console.WriteLine($"      accepted on this machine: v{settings.EulaVersionAccepted} " +
                              (settings.EulaVersionAccepted >= eula.Version ? "(current — the tab opens)" : "(the tab would gate)"));
        }
        else
        {
            Console.WriteLine($"FAIL  notice: {eula.Error}");
        }

        Console.WriteLine();
        var lanes = await MasterServerClient.GetDownloadStatusAsync(url, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine(lanes.Reachable
            ? $"OK    lanes: setup={Flag(lanes.Setup)} content={Flag(lanes.Content)} " +
              $"platform={Flag(lanes.Platform)} dedi={Flag(lanes.Dedi)}"
            : "WARN  lanes: /launcher/status did not answer (PLAY does not require an update when it is down)");

        Console.WriteLine();
        Console.WriteLine("Nothing was written: no notice accepted, no setting changed, no server joined.");
        return result.Success ? 0 : 1;
    }

    static string Flag(bool on) => on ? "on" : "off";

    static string Trim(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
