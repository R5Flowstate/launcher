using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// The server browser, offline. Everything here is the same code the browser
/// runs — the parser, the row derivation, the ordering, the favourite identity,
/// the address refusal, the lane flags — against fixtures rather than the live
/// master server, so the pass is deterministic and no socket is opened.
///
/// The one exception is the locked-tab check: it drives the real refresh path and
/// proves the request is never made when the notice has not been accepted.
/// </summary>
static class BrowserSelfTest
{
    public static void Run(Action<string, bool, string> check, MainViewModel vm)
    {
        // Most checks read fine without a detail line, so it is optional here the
        // way SelfTestRunner's own Check has it (an Action<...> cannot carry the
        // default itself).
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // --- the parser's own fixtures, straight from Contracts ------------
        var fixtureError = ServerListingParser.SelfCheck();
        c("browser: ServerListingParser self-check", fixtureError is null, fixtureError ?? "all fixtures pass");

        // --- a real answer, shaped like the master server's ----------------
        const string Live = """
            {"success":true,"servers":[
              {"name":"Cafe Bogota","description":"d","hidden":false,"map":"mp_rr_divided_moon_mu1",
               "playlist":"survival_dev","ip":"203.0.113.9","port":37015,"key":"k1","checksum":1,
               "numPlayers":7,"maxPlayers":60,"hasPassword":false},
              {"name":"Quiet Lobby","description":"d","hidden":false,"map":"mp_lobby",
               "playlist":"lobby","ip":"203.0.113.10","port":37016,"key":"k2","checksum":2,
               "numPlayers":1,"maxPlayers":60,"hasPassword":true},
              {"name":"Modded","description":"d","hidden":false,"map":"mp_rr_canyonlands_mu1",
               "playlist":"survival","ip":"2001:db8::1","port":37017,"key":"k3","checksum":3,
               "numPlayers":3,"maxPlayers":12,"hasPassword":false,"requiredMods":["a","b"]},
              {"name":"Hidden","description":"d","hidden":true,"map":"mp_lobby","playlist":"lobby",
               "ip":"203.0.113.11","port":37018,"key":"k4","checksum":4,"numPlayers":9,"maxPlayers":9}
            ]}
            """;

        var parsed = ServerListingParser.Parse(Live);
        c("browser: live-shaped answer parses", parsed.Success, parsed.Error ?? "ok");
        c("browser: hidden rows never reach the list", parsed.Servers.Count == 3,
            $"{parsed.Servers.Count} row(s), {parsed.Dropped.Count} dropped of {parsed.RawCount} raw");
        c("browser: fullest server first", parsed.Servers[0].Name == "Cafe Bogota",
            string.Join(" > ", parsed.Servers.Select(s => $"{s.Name}({s.NumPlayers})")));
        c("browser: no update notice on a normal list", !parsed.UpdateRequired);

        // --- rows ----------------------------------------------------------
        var row = vm.ToRow(parsed.Servers[0]);
        c("row: address label is host:port", row.AddressLabel == "203.0.113.9:37015", row.AddressLabel);
        c("row: key identity is ip:port", row.Key == "203.0.113.9:37015", row.Key);
        c("row: players label", row.PlayersLabel == "7/60", row.PlayersLabel);
        // The stem stays visible (it is what +map receives, and what a bug report
        // quotes) and the name the install gives it rides in brackets — the
        // playlist catalog is what supplies that name now, so this reads the same
        // as Windows on an install that ships the map.
        c("row: map label keeps the stem, and names it when the catalog can",
            row.MapLabel.StartsWith("mp_rr_divided_moon_mu1")
            && row.MapLabel.Contains("mp_rr_divided_moon_mu1", StringComparison.Ordinal),
            row.MapLabel);
        c("row: detail line carries map, playlist, players and address",
            row.Detail.Contains("survival_dev") && row.Detail.Contains("7/60")
            && row.Detail.Contains("203.0.113.9:37015"), row.Detail);
        c("row: an open server is joinable and says so",
            row.CanJoin && row.JoinEnabled && row.JoinCaption == Loc.Get("join"), row.JoinCaption);
        c("row: join hint names the address", row.JoinHint.Contains("203.0.113.9:37015"), row.JoinHint);
        c("row: no mod badge on a clean server",
            !row.HasModRequirement && row.ModBadge.Length == 0, row.ModBadge);

        var locked = vm.ToRow(parsed.Servers[2]);
        c("row: passworded row is still joinable (the client asks)", locked.CanJoin);
        c("row: passworded row says the client asks",
            locked.HasPassword && locked.JoinHint == Loc.Get("join_hint_password"), locked.JoinHint);

        var modded = vm.ToRow(parsed.Servers[1]);
        c("row: ipv6 address is bracketed", modded.AddressLabel == "[2001:db8::1]:37017", modded.AddressLabel);
        c("row: mod badge counts the requirements", modded.ModBadge == Loc.Format("mods_req_n", 2), modded.ModBadge);
        c("row: mod tooltip lists the mods",
            modded.HasModRequirement && modded.ModTooltip.Contains("a") && modded.ModTooltip.Contains("b"),
            modded.ModTooltip.Replace("\n", " / "));

        var blank = vm.ToRow(new ServerListing { Name = "   ", Ip = "203.0.113.9", Port = 1 });
        c("row: an unnamed server is not blank", blank.Name == Loc.Get("unnamed"), blank.Name);

        // A row that cannot be joined is not clickable, and a settling hop locks
        // the row until it lands (Windows: JoinEnabled).
        var dead = vm.ToRow(new ServerListing { Name = "Synthetic", Ip = "127.0.0.1", Port = 37015 });
        c("row: loopback/filler rows are not joinable", !dead.CanJoin && !dead.JoinEnabled, dead.JoinHint);
        var steering = vm.ToRow(parsed.Servers[0]);
        steering.IsSteering = true;
        c("row: a settling hop locks the row", !steering.JoinEnabled);
        steering.IsSteering = false;
        c("row: the row unlocks when the hop lands", steering.JoinEnabled);

        // --- the star ------------------------------------------------------
        var star = vm.ToRow(parsed.Servers[0]);
        c("row: unstarred glyph is hollow", star.FavoriteGlyph == "☆", star.FavoriteGlyph);
        star.IsFavorite = true;
        c("row: starred glyph is solid", star.FavoriteGlyph == "★", star.FavoriteGlyph);

        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        c("fav: starring adds the key", MainViewModel.ToggleFavoriteKey(favorites, "203.0.113.9:37015")
            && favorites.Contains("203.0.113.9:37015"));
        c("fav: starring again removes it",
            !MainViewModel.ToggleFavoriteKey(favorites, "203.0.113.9:37015") && favorites.Count == 0);
        MainViewModel.ToggleFavoriteKey(favorites, "203.0.113.9:37015");
        c("fav: identity is case-insensitive on the host",
            favorites.Contains("203.0.113.9:37015"));

        // --- favourites survive a restart through settings.json ------------
        var raw = LinuxSettings.FormatFavorites(new[] { "b:2", "a:1", "B:2", "  ", "a:1" });
        c("fav: written sorted, trimmed and de-duplicated", raw == "a:1\nb:2", raw.Replace("\n", " | "));
        var back = LinuxSettings.ParseFavorites(raw);
        c("fav: read back in order", back.Length == 2 && back[0] == "a:1" && back[1] == "b:2",
            string.Join(",", back));
        c("fav: an empty setting reads as no favourites",
            LinuxSettings.ParseFavorites("").Length == 0
            && LinuxSettings.ParseFavorites(null).Length == 0
            && LinuxSettings.FormatFavorites(Array.Empty<string>()).Length == 0);
        c("fav: a round trip is idempotent", LinuxSettings.FormatFavorites(back) == raw);
        c("fav: a hand-edited file with stray blanks still reads",
            LinuxSettings.ParseFavorites("\n\r\n 203.0.113.9:37015 \n\n").Length == 1);

        // --- ordering: favourites, then players, then name -----------------
        var rows = new List<ServerRowVM>
        {
            vm.ToRow(new ServerListing { Name = "Bravo", Ip = "10.0.0.2", Port = 2, NumPlayers = 2, MaxPlayers = 10 }),
            vm.ToRow(new ServerListing { Name = "alpha", Ip = "10.0.0.1", Port = 1, NumPlayers = 2, MaxPlayers = 10 }),
            vm.ToRow(new ServerListing { Name = "Full", Ip = "10.0.0.3", Port = 3, NumPlayers = 9, MaxPlayers = 10 }),
            vm.ToRow(new ServerListing { Name = "Starred", Ip = "10.0.0.4", Port = 4, NumPlayers = 1, MaxPlayers = 10 }),
        };
        MainViewModel.ApplyFavorites(rows, new[] { "10.0.0.4:4" });
        MainViewModel.SortServerRows(rows);
        var order = string.Join(",", rows.ConvertAll(r => r.Name));
        c("order: favourite first, then players, then name (case-insensitive)",
            order == "Starred,Full,alpha,Bravo", order);
        c("order: the starred row really is the favourite row", rows[0].IsFavorite && rows[0].Key == "10.0.0.4:4");

        // --- the address the browser hands the game ------------------------
        c("addr: plain host and port", ConnectTarget.TryFormat("203.0.113.9", 37015, out var t1) && t1 == "203.0.113.9:37015", t1);
        c("addr: bare ipv6 gets bracketed", ConnectTarget.TryFormat("2001:db8::1", 37015, out var t2) && t2 == "[2001:db8::1]:37015", t2);
        c("addr: an already-bracketed ipv6 is left alone", ConnectTarget.TryFormat("[2001:db8::1]", 37015, out var t3) && t3 == "[2001:db8::1]:37015", t3);
        c("addr: port 0 is refused", !ConnectTarget.TryFormat("203.0.113.9", 0, out _));
        c("addr: port out of range is refused",
            !ConnectTarget.TryFormat("203.0.113.9", 65536, out _)
            && !ConnectTarget.TryFormat("203.0.113.9", -1, out _));
        c("addr: a command separator is refused",
            !ConnectTarget.TryFormat("host;disconnect", 1, out _)
            && !ConnectTarget.TryFormat("host\nquit", 1, out _));
        c("addr: spaces and quotes are refused",
            !ConnectTarget.TryFormat("host name", 1, out _)
            && !ConnectTarget.TryFormat("host\"x", 1, out _));
        c("addr: an empty or alnum-free host is refused",
            !ConnectTarget.TryFormat("", 1, out _)
            && !ConnectTarget.TryFormat(":::", 1, out _)
            && !ConnectTarget.TryFormat("...", 1, out _));
        c("addr: an over-long host is refused",
            !ConnectTarget.TryFormat(new string('a', 65), 1, out _));
        c("addr: hostnames keep their dots, dashes and underscores",
            ConnectTarget.IsSafeHost("my-server_1.example.com"));
        c("addr: a blank host in the display form becomes loopback",
            ConnectTarget.Format(null, 37015) == "127.0.0.1:37015", ConnectTarget.Format(null, 37015));
        c("addr: a bad port in the display form becomes the dedi default",
            ConnectTarget.Format("x", 0) == $"x:{ConnectTarget.DefaultDediPort}", ConnectTarget.Format("x", 0));

        // --- the wire version the master gates on --------------------------
        var tmp = Path.Combine(Path.GetTempPath(), "r5f-wire-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        try
        {
            c("wire: no install and no gate falls back to the compiled version",
                VersionIdentity.ResolveExpectedWireVersion(tmp, null) == VersionIdentity.CompiledSdkVersion,
                VersionIdentity.ResolveExpectedWireVersion(tmp, null));
            c("wire: the channel's gate is used when there is no stamp",
                VersionIdentity.ResolveExpectedWireVersion(tmp, "GATE-9") == "GATE-9");
            File.WriteAllText(Path.Combine(tmp, ProductConstants.SdkVersionStampFileName), "STAMPED-1\n");
            c("wire: the install's own stamp wins over the gate",
                VersionIdentity.ResolveExpectedWireVersion(tmp, "GATE-9") == "STAMPED-1",
                VersionIdentity.ResolveExpectedWireVersion(tmp, "GATE-9"));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }

        // --- the download lanes --------------------------------------------
        var all = MasterServerClient.ParseDownloadStatus("""{"setup":true,"content":true,"platform":true}""");
        c("lanes: a full answer reads through",
            all.Reachable && all.Setup && all.Content && all.Platform && all.AnyLane);
        var open = MasterServerClient.ParseDownloadStatus("{}");
        c("lanes: an empty object defaults every lane on but leaves dedi off (fails closed)",
            open.Reachable && open.Setup && open.Content && open.Platform && !open.Dedi);
        var closed = MasterServerClient.ParseDownloadStatus("""{"content":false,"platform":false,"dedi":true}""");
        c("lanes: a closed answer is respected", !closed.AnyLane && closed.Dedi);
        c("lanes: garbage is unreachable, not a closed lane",
            !MasterServerClient.ParseDownloadStatus("<html>502</html>").Reachable
            && !MasterServerClient.ParseDownloadStatus("").Reachable);
        c("lanes: a JSON array is not a status object",
            !MasterServerClient.ParseDownloadStatus("[1,2]").Reachable);
        c("lanes: unreachable still lets INSTALL try the CDN",
            DownloadStatus.Unreachable.AnyLane && !DownloadStatus.Unreachable.Reachable);

        // --- https only ------------------------------------------------------
        // Both of these are refused before a socket is opened, so the check is
        // offline: only https (or loopback http) may carry a version or a query.
        var plain = MasterServerClient.ListServersAsync("http://master.example.com", "v1", null)
            .GetAwaiter().GetResult();
        c("transport: plain http to a remote host is refused",
            !plain.Success && (plain.Error?.Contains("https") ?? false), plain.Error ?? "(no error)");
        var badPublic = MasterServerClient.GetPublicAsync("http://master.example.com", "/x")
            .GetAwaiter().GetResult();
        c("transport: GetPublicAsync refuses plain http too",
            !badPublic.Ok && badPublic.Error!.Contains("https"), badPublic.Error ?? "(no error)");
        var noWire = MasterServerClient.ListServersAsync("https://master.example.com", "", null)
            .GetAwaiter().GetResult();
        c("transport: an empty wire version never reaches the network",
            !noWire.Success && noWire.Error == Loc.Get("ms_wire_empty"), noWire.Error ?? "(no error)");
        c("transport: the master server is https and default",
            MainViewModel.MasterServerUrl == ProductConstants.DefaultMasterServerUrl.TrimEnd('/'),
            MainViewModel.MasterServerUrl);

        // --- whether an open lane blocks PLAY -------------------------------
        // Windows' RefreshSimplePlayButton gate: only an *open* lane that is
        // serving the track that is behind may refuse a launch. Everything else
        // lets the player in.
        static InstallHealthReport Health(InstallHealthStatus client, InstallHealthStatus platform) => new()
        {
            Enforced = true,
            Client = new TrackHealth { Preset = "client", Status = client },
            Platform = new TrackHealth { Preset = "platform", Status = platform },
        };

        var allOpen = MasterServerClient.ParseDownloadStatus("""{"content":true,"platform":true}""");
        var contentOff = MasterServerClient.ParseDownloadStatus("""{"content":false,"platform":false}""");
        var behind = Health(InstallHealthStatus.UpdateAvailable, InstallHealthStatus.Ready);
        var current = Health(InstallHealthStatus.Ready, InstallHealthStatus.Ready);

        c("play gate: an open content lane refuses play on a behind client",
            MainViewModel.LaneBlocksPlay(behind, allOpen, requireClient: true, requireServer: false));
        c("play gate: an up-to-date install plays",
            !MainViewModel.LaneBlocksPlay(current, allOpen, requireClient: true, requireServer: false));
        c("play gate: a shut lane lets the player in",
            !MainViewModel.LaneBlocksPlay(behind, contentOff, requireClient: true, requireServer: false));
        c("play gate: an unreachable master never blocks a launch",
            !MainViewModel.LaneBlocksPlay(behind, DownloadStatus.Unreachable, requireClient: true, requireServer: false));
        var serverBehind = new InstallHealthReport
        {
            Enforced = true,
            Client = new TrackHealth { Preset = "client", Status = InstallHealthStatus.Ready },
            Server = new TrackHealth { Preset = "server", Status = InstallHealthStatus.UpdateAvailable },
        };
        c("play gate: a client-only check ignores a behind server",
            !MainViewModel.LaneBlocksPlay(serverBehind, allOpen, requireClient: true, requireServer: false));
        c("play gate: asking about the server too does block it",
            MainViewModel.LaneBlocksPlay(serverBehind, allOpen, requireClient: true, requireServer: true));
        c("play gate: a behind platform blocks only while its lane is open",
            MainViewModel.LaneBlocksPlay(
                Health(InstallHealthStatus.Ready, InstallHealthStatus.UpdateAvailable), allOpen, true, false)
            && !MainViewModel.LaneBlocksPlay(
                Health(InstallHealthStatus.Ready, InstallHealthStatus.UpdateAvailable), contentOff, true, false));

        // --- the install preflight -------------------------------------------
        // Two throwaway folders, never the player's install: one empty (a first
        // install) and one with the marker file LooksLikeInstall checks.
        var probe = Path.Combine(Path.GetTempPath(), "r5f-install-gate-" + Environment.ProcessId);
        var emptyDir = Path.Combine(probe, "empty");
        var stubDir = Path.Combine(probe, "stubbed");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(stubDir);
        File.WriteAllText(Path.Combine(stubDir, "r5apex.exe"), "stub");
        try
        {
            c("install gate: a fully shut master refuses the job",
                !vm.LaneOpenForInstall(contentOff) && vm.InstallStatus == Loc.Get("status_downloads_off"),
                vm.InstallStatus);
            c("install gate: an unreachable master still lets the CDN be tried",
                vm.LaneOpenForInstall(DownloadStatus.Unreachable), "any lane open");
            c("install gate: a shut content lane refuses a first install",
                !vm.LaneOpenForInstall(
                    MasterServerClient.ParseDownloadStatus("""{"content":false,"platform":true}"""),
                    emptyDir),
                emptyDir);
            c("install gate: a shut content lane is not a refusal once files exist",
                vm.LaneOpenForInstall(
                    MasterServerClient.ParseDownloadStatus("""{"content":false,"platform":true}"""),
                    stubDir),
                stubDir);
        }
        finally
        {
            try { Directory.Delete(probe, recursive: true); } catch { }
        }

        c("dedi: the package link is closed on a fresh gate (no round trip yet)",
            !vm.DediPackageAvailable && vm.CachedDownloadGate.Reachable, "fail closed");
        c("dedi: the link follows the master's dedi flag",
            MasterServerClient.ParseDownloadStatus("""{"dedi":true}""").Dedi
            && !MasterServerClient.ParseDownloadStatus("""{"content":true}""").Dedi);
        c("dedi: the url is the one the Windows launcher opens",
            MainViewModel.DediPackageUrl == ProductConstants.DediPackageUrl, MainViewModel.DediPackageUrl);

        // --- the tab gate ----------------------------------------------------
        // The window hands its gate in; a locked browser must clear its list and
        // say why without opening a connection. Restored afterwards so the rest
        // of the pass (and the GUI) keeps the window's own gate.
        var savedProbe = vm.ServersUnlockedProbe;
        try
        {
            c("gate: the window hands the browser its unlock state", savedProbe is not null);
            // The rule, stated as a property of the two numbers it is made of.
            // Asserting the live probe instead would be asserting what is in the
            // player's settings file: a green check on a fresh install and a red
            // one the moment somebody accepts the notice, which is not a fact
            // about this code at all.
            c("gate: a fresh config accepts nothing, so the tab is locked",
                !MainViewModel.ServersUnlockedFor(0, 0) && !MainViewModel.ServersUnlockedFor(0, 4),
                "accepted 0");
            c("gate: the accepted version unlocks, an older one does not",
                MainViewModel.ServersUnlockedFor(4, 4)
                && !MainViewModel.ServersUnlockedFor(3, 4)
                && MainViewModel.ServersUnlockedFor(1, 0), "version rule");

            vm.Servers.Add(vm.ToRow(parsed.Servers[0]));
            var asked = 0;
            vm.ServersUnlockedProbe = () => { asked++; return false; };
            vm.RefreshServerListAsync(quiet: false).GetAwaiter().GetResult();
            c("gate: the refresh path consults the gate", asked == 1, $"{asked} ask(s)");
            c("gate: a locked refresh clears the list", vm.Servers.Count == 0, $"{vm.Servers.Count} row(s) left");
            c("gate: a locked refresh says what to do",
                vm.BrowserStatus == Loc.Get("status_eula_servers"), Clip(vm.BrowserStatus));
            c("gate: a locked refresh is not busy", !vm.BrowserBusy);
            // The locked path returns before the wire version is even resolved,
            // so no socket is opened — the suite stays offline (--servers is the
            // live probe, and the locked branch is what it cannot cover).
        }
        finally
        {
            vm.ServersUnlockedProbe = savedProbe;
            vm.Servers.Clear();
            vm.BrowserStatus = "";
        }
    }

    static string Clip(string s) => s.Length <= 70 ? s : s[..69] + "…";
}

