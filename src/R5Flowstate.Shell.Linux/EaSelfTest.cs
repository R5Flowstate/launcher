using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using R5Flowstate.Content.Rpak;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// The EA side: Proton's prefix, Proton's own Wine, the redistributable prep, the
/// identity classifier, the loopback channel between the game and the EA App, the
/// loadscreen art cache with the records the hosted decoder answers through, the
/// display lane behind the Res button, and the hosted console — the relay's
/// environment contract, its handshake, and the socket tap the Console tab reads.
/// The split runtime — the EA App under a system Wine in a prefix of its own — is
/// gone, and the groups that existed only to pin it (Wine discovery, the Wine
/// spawner, wineboot bootstrap, the install shim, the registry import) went with it
/// rather than being kept as tests of deleted code.
///
/// Hermetic by construction. Nothing is executed: the spawner is only asked to
/// <em>construct</em> a start info, every prefix fixture is a directory tree this
/// suite writes under its own temp root, the Proton build is two empty files named
/// <c>wine</c> and <c>wineserver</c>, and the channel probe is pointed at a
/// listener this suite opens on an ephemeral port rather than at EA's own 3216.
/// The display checks point the two enumeration seams at fixture text instead of at
/// this session, so no tool is run and no monitor is asked anything. The console
/// checks stand a listener in for the relay and hand the tap this process's own
/// handle as its child, so no relay, no Proton and no game are needed for them.
/// Nothing creates <c>~/Games/r5flowstate</c>, and the player's <c>settings.json</c>
/// is read by nothing here — the migration is asserted against
/// <see cref="LinuxSettings.Serialize"/>, in memory.
/// </summary>
static class EaSelfTest
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var root = Path.Combine(Path.GetTempPath(), "r5f-ea-selftest-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(root);
            LayoutChecks(check, Path.Combine(root, "layout"));
            LogLayoutChecks(check, Path.Combine(root, "logs"));
            MigrationChecks(check);
            ConstructionChecks(check, Path.Combine(root, "construct"));
            ProbeChecks(check);
            BridgeChecks(check, Path.Combine(root, "bridge"));
            PrepChecks(check, Path.Combine(root, "prep"));
            ArtCacheChecks(check, Path.Combine(root, "art"));
            ArtDecodeChecks(check, Path.Combine(root, "artdecode"));
            WinHostChecks(check);
            DisplayModesChecks(check);
            HostedConsoleChecks(check, Path.Combine(root, "console"));
        }
        catch (Exception ex)
        {
            check("ea runtime", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------ settings

    /// <summary>
    /// The one thing left of the runtime setting: a file that named the Wine era
    /// still loads, says so once, and loses the keys on the next save.
    ///
    /// Deserialized in memory and serialized in memory. The player's
    /// <c>settings.json</c> is not this test's business, and the suite rule is that
    /// its bytes do not change — which is exactly why the round trip goes through
    /// <see cref="LinuxSettings.Serialize"/> instead of <c>Save</c>.
    /// </summary>
    static void MigrationChecks(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        LinuxSettings Parse(string json)
            => System.Text.Json.JsonSerializer.Deserialize<LinuxSettings>(json)!;

        // Every spelling a Wine-era file might carry, plus the two that must *not*
        // be read as Wine at all. The list is deliberately wider than the enum was:
        // a hand-edited file is the case this exists for.
        var spellings = new[] { "wine", "WINE", "system-wine", "SystemWine", "split", "shared-prefix" };
        var missed = spellings.FirstOrDefault(s =>
            !Parse($$"""{"PrefixPath":"/p","EaRuntime":"{{s}}"}""").MigratedFromSplitRuntime);
        check($"settings: {spellings.Length} Wine-era spellings are all recognised as one",
            missed is null, missed ?? "all recognised");

        var notWine = new[] { "proton", "", "nonsense" };
        var false_ = notWine.FirstOrDefault(s =>
            Parse($$"""{"PrefixPath":"/p","EaRuntime":"{{s}}"}""").MigratedFromSplitRuntime);
        check("settings: a file that was already Proton-only is not told it migrated",
            false_ is null, false_ ?? "none flagged");

        var wine = Parse("""{"PrefixPath":"/p","EaRuntime":"wine","EaPrefixPath":"/ea"}""");
        check("settings: a Wine-era file loads rather than being refused",
            wine.PrefixPath == "/p" && wine.EaRuntime == "wine" && wine.EaPrefixPath == "/ea",
            $"{wine.PrefixPath} / {wine.EaRuntime} / {wine.EaPrefixPath}");

        check("settings: the migration is flagged, and the notice says what changed",
            wine.MigratedFromSplitRuntime
            && wine.MigrationNotice is { } n
            && n.Contains("share the game's Proton prefix")
            && n.Contains("dropped on the next save"),
            wine.MigrationNotice ?? "(no notice)");

        check("settings: the notice is null for a file that was already Proton-only",
            Parse("""{"PrefixPath":"/p","EaRuntime":"proton"}""").MigrationNotice is null
            && new LinuxSettings().MigrationNotice is null);

        // The save half: the keys go, everything else stays. Both keys, and only
        // those two — a migration that dropped PrefixPath would be worse than the
        // setting it removes.
        var saved = wine.Serialize();
        check("settings: the next save drops both Wine-era keys",
            !saved.Contains("\"EaRuntime\"", StringComparison.Ordinal)
            && !saved.Contains("\"EaPrefixPath\"", StringComparison.Ordinal),
            saved.Replace('\n', ' '));

        check("settings: the rest of the file survives the save",
            saved.Contains("\"PrefixPath\": \"/p\"", StringComparison.Ordinal)
            && Parse(saved).PrefixPath == "/p",
            saved.Replace('\n', ' '));

        var fresh = new LinuxSettings();
        check("settings: defaults are Proton-only, with the GE fallback on",
            fresh.MigratedFromSplitRuntime == false
            && fresh.MigrationNotice is null
            && fresh.StartEaWithGame
            && fresh.AutoDownloadProtonGe,
            $"ge={fresh.AutoDownloadProtonGe}");

        check("settings: a default prefix is the game's, and there is no second one",
            LinuxSettings.DefaultPrefixPath.EndsWith(
                Path.Combine("Games", "r5flowstate"), StringComparison.Ordinal),
            LinuxSettings.DefaultPrefixPath);
    }

    // ------------------------------------------------------------------- channel

    /// <summary>
    /// The loopback probe, against a listener this suite opens itself on an
    /// ephemeral port — never against 3216 or 3215, which belong to whatever EA is
    /// running on the machine. The synthetic verdict cases below are built from
    /// <see cref="PortProbe"/> values directly for the same reason: a test that
    /// wants to say "something is listening on 3215" must not actually listen
    /// there.
    /// </summary>
    static void ProbeChecks(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        check("probe: the ports are EA's LSX pair, LSX first",
            EaChannelProbe.LsxPort == 3216 && EaChannelProbe.LocalHostPort == 3215
            && EaChannelProbe.DefaultPorts.Count == 2
            && EaChannelProbe.DefaultPorts[0] == EaChannelProbe.LsxPort);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var open = EaChannelProbe.ProbePort(port);
            check("probe: a listening port reads Open, and names the family that answered",
                open.IsOpen && open.Family == "IPv4" && open.Describe().Contains("IPv4"),
                open.Describe());

            // An ephemeral port, so 3216 is not in the list: the verdict must report
            // what it heard without pretending to judge EA's channel.
            var (line, listening) = EaChannelProbe.Verdict(new[] { open });
            check("probe: a verdict agrees with IsListening, and never invents 3216",
                listening == EaChannelProbe.IsListening(new[] { open })
                && listening && line.Contains("listening")
                && line.Contains(EaChannelProbe.LsxPort.ToString()) && line.Contains("was not probed"),
                line);

            // The whole point of probing both families: v6 was refused above and
            // the v4 answer is what made this Open, which is what a v4-only
            // Wine-hosted listener looks like.
            check("probe: the detail records what each family said",
                open.Detail.Contains("IPv6") && open.Detail.Contains("IPv4"), open.Detail);
        }
        finally
        {
            listener.Stop();
        }

        var closed = EaChannelProbe.ProbePort(port);
        check("probe: a stopped listener reads Refused, not Open",
            !closed.IsOpen && closed.State == LoopbackState.Refused, closed.Describe());

        // A port nothing has ever bound. Same answer, and it must not be Open.
        var never = EaChannelProbe.ProbePort(FreePort());
        check("probe: a port with no listener never reads Open",
            !never.IsOpen, never.Describe());

        // Synthetic verdicts — the shapes the launcher will show.
        var lsxOnly = new[]
        {
            new PortProbe(EaChannelProbe.LsxPort, LoopbackState.Refused, "IPv6", ""),
            new PortProbe(EaChannelProbe.LocalHostPort, LoopbackState.Open, "IPv4", ""),
        };
        var (hostLine, hostListening) = EaChannelProbe.Verdict(lsxOnly);
        check("probe: EA's host service answering is not the same as the LSX channel",
            !hostListening && !EaChannelProbe.IsListening(lsxOnly)
            && hostLine.Contains(EaChannelProbe.LsxPort.ToString())
            && hostLine.Contains("identity will not arrive"),
            hostLine);

        var (deadLine, dead) = EaChannelProbe.Verdict(new[]
        {
            new PortProbe(EaChannelProbe.LsxPort, LoopbackState.Refused, "IPv6", ""),
            new PortProbe(EaChannelProbe.LocalHostPort, LoopbackState.Refused, "IPv6", ""),
        });
        check("probe: nothing listening says so, in words a player reads",
            !dead && deadLine.Contains("nothing is listening"), deadLine);

        var (silentLine, silent) = EaChannelProbe.Verdict(new[]
        {
            new PortProbe(EaChannelProbe.LsxPort, LoopbackState.TimedOut, "IPv6", ""),
        });
        check("probe: a bound-but-not-accepting port is timed out, not refused",
            !silent && silentLine.Contains("TimedOut") && silentLine.Contains("nothing accepted"),
            silentLine);

        var (noneLine, noneListening) = EaChannelProbe.Verdict(Array.Empty<PortProbe>());
        check("probe: an empty probe is not a failure claim",
            !noneListening && noneLine.Contains("not probed"), noneLine);

        check("probe: every state has a word",
            new[] { LoopbackState.Open, LoopbackState.Refused, LoopbackState.TimedOut,
                    LoopbackState.Unreachable, LoopbackState.Error }
                .All(s => EaChannelProbe.Describe(s).Length > 0));
    }

    /// <summary>A port that was bound and released, so it is certainly free — and
    /// not one of EA's, which the suite must never occupy.</summary>
    static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ----------------------------------------------------------- prefix layouts

    /// <summary>
    /// The layout rules, Proton-only now: one prefix shape, one place that knows
    /// where its <c>drive_c</c> is, and the tolerant case that helper has always
    /// had — a compatdata root that is really a Wine prefix is read as one, because
    /// a hand-pointed prefix should not need the launcher's permission to work.
    /// </summary>
    static void LayoutChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        check("layout: a Proton prefix keeps its wine prefix under pfx/",
            PrefixLayout.WinePrefixOf("/p") == Path.Combine("/p", "pfx"));

        var proton = Path.Combine(root, "proton");
        Directory.CreateDirectory(Path.Combine(proton, "pfx", "drive_c"));
        check("layout: a compatdata root resolves to pfx/drive_c",
            PrefixLayout.DriveCFor(proton) == Path.Combine(proton, "pfx", "drive_c"));

        var pointed = Path.Combine(root, "pointed");
        Directory.CreateDirectory(Path.Combine(pointed, "drive_c"));
        check("layout: a prefix pointed straight at drive_c still resolves",
            PrefixLayout.DriveCFor(pointed) == Path.Combine(pointed, "drive_c"));

        check("layout: a prefix that is not there resolves to a path, not an exception",
            PrefixLayout.DriveCFor(Path.Combine(root, "absent"))
                == Path.Combine(root, "absent", "pfx", "drive_c"),
            PrefixLayout.DriveCFor(Path.Combine(root, "absent")));

        var fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(fresh);
        check("layout: an empty dir has no entries and is not bootstrapped",
            !PrefixLayout.HasEntries(fresh)
            && !PrefixLayout.IsBootstrapped(fresh)
            && PrefixLayout.DeclaredArch(fresh) is null
            && !PrefixLayout.IsProtonPrefix(fresh));

        var built = Path.Combine(root, "built");
        Directory.CreateDirectory(built);
        File.WriteAllText(PrefixLayout.SystemRegPath(built),
            "WINE REGISTRY Version 2\n;; All keys relative to \\\\Machine\n\n#arch=win64\n");
        check("layout: a bootstrapped prefix reports its arch",
            PrefixLayout.IsBootstrapped(built)
            && PrefixLayout.DeclaredArch(built) == "win64");

        // Proton's own marker. It is what the prep's refusal reads to keep this
        // launcher out of a prefix somebody else built, so it gets a fixture of its
        // own rather than riding along on the arch check above.
        var guarded = Path.Combine(root, "guarded", "pfx");
        Directory.CreateDirectory(guarded);
        check("layout: a Wine-bootstrapped prefix is not read as Proton's",
            !PrefixLayout.IsProtonPrefix(built) && !PrefixLayout.IsProtonPrefix(guarded));

        File.WriteAllText(Path.Combine(guarded, "creation_sync_guard"), "");
        check("layout: Proton's own marker is what makes a prefix Proton's",
            PrefixLayout.IsProtonPrefix(guarded)
            && !PrefixLayout.IsProtonPrefix(Path.Combine(root, "absent")));

        // A path on the way to a Windows program. Wine's Z: is /, so every host
        // path handed to something inside the prefix goes through this — and a path
        // that is already Windows-shaped must come back untouched, because
        // round-tripping it would turn C:\x into Z:\C:\x.
        check("layout: a host path becomes a Z: path, and a Windows one is left alone",
            PrefixLayout.ToZDrive("/home/p/Games/r5flowstate") == @"Z:\home\p\Games\r5flowstate"
            && PrefixLayout.ToZDrive(@"C:\Program Files\EA") == @"C:\Program Files\EA"
            && PrefixLayout.ToZDrive(@"\\server\share") == @"\\server\share"
            && PrefixLayout.ToZDrive("relative/path") == "relative/path"
            && PrefixLayout.ToZDrive("") == "",
            PrefixLayout.ToZDrive("/home/p/Games/r5flowstate"));
    }

    /// <summary>
    /// Where the EA App's own log is read from. The bug the split exposed was here:
    /// <c>FindNewestEaLog</c> hardcoded Proton's <c>pfx/drive_c</c>, so an EA prefix
    /// in another shape reported "no log to read" forever. There is one shape now,
    /// but the reader goes through the same layout helper as the exe finder, so the
    /// two cannot drift apart again.
    /// </summary>
    static void LogLayoutChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        string Drop(string driveC, string name)
        {
            var dir = Path.Combine(driveC, "users", "steamuser", "AppData", "Local", "Temp");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name);
            File.WriteAllText(file, "EA installer log\n");
            return file;
        }

        var proton = Path.Combine(root, "proton-prefix");
        var protonLog = Drop(Path.Combine(proton, "pfx", "drive_c"), "EA_app_proton.log");
        check("EA log: a Proton prefix is read at pfx/drive_c",
            ProtonLauncher.FindNewestEaLog(proton) == protonLog,
            ProtonLauncher.FindNewestEaLog(proton) ?? "none");

        // The tolerant shape, which is the one the reader used to be blind to.
        var pointed = Path.Combine(root, "pointed-prefix");
        var pointedLog = Drop(Path.Combine(pointed, "drive_c"), "EA_app_pointed.log");
        check("EA log: a prefix pointed straight at drive_c is read too",
            ProtonLauncher.FindNewestEaLog(pointed) == pointedLog,
            ProtonLauncher.FindNewestEaLog(pointed) ?? "none");

        check("EA log: no prefix is still null, not a throw",
            ProtonLauncher.FindNewestEaLog(null) is null
            && ProtonLauncher.FindNewestEaLog("") is null
            && ProtonLauncher.FindNewestEaLog(Path.Combine(root, "absent")) is null);

        // The newest log wins, and it is the *newest* rather than the first one
        // found in directory order — an install that failed twice leaves two logs
        // and the second one is the one that says why.
        var two = Path.Combine(root, "two-logs-prefix");
        var older = Drop(Path.Combine(two, "pfx", "drive_c"), "EA_app_older.log");
        var newer = Drop(Path.Combine(two, "pfx", "drive_c"), "EA_app_newer.log");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        check("EA log: the newest log is the one read",
            ProtonLauncher.FindNewestEaLog(two) == newer,
            ProtonLauncher.FindNewestEaLog(two) ?? "none");

        check("EA log: an exe is found where the log is, on the same layout rule",
            ProtonLauncher.FindEaDesktopExe("") is null
            && ProtonLauncher.FindEaDesktopExe("   ") is null
            && ProtonLauncher.IsEaInstalled(Path.Combine(root, "absent")) == false);
    }

    // -------------------------------------------------------- spawn construction

    /// <summary>
    /// What the launcher hands the OS, built and read rather than run: the seam is
    /// <see cref="ProtonLauncher.BuildStartInfo"/>, internal for exactly this
    /// reason. It is one shape now, which is why this group lost its Wine half —
    /// the two-arm dispatch it used to pin is gone, and what is left is the
    /// environment every Windows program this launcher starts gets.
    /// </summary>
    static void ConstructionChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        Directory.CreateDirectory(root);
        var protonDir = Path.Combine(root, "proton-build");
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");
        var prefix = Path.Combine(root, "game-prefix");

        var info = ProtonLauncher.BuildStartInfo(
            new ProtonLauncher.ProtonRunOptions(protonDir, prefix, "/tmp/game/r5apex.exe",
                "-nodiscord"),
            capture: true);

        check("launch: it goes through the proton wrapper, not a wine binary",
            Path.GetFullPath(info.FileName) == Path.GetFullPath(Path.Combine(protonDir, "proton")),
            info.FileName);

        var args = info.ArgumentList.ToArray();
        check("launch: run, then the exe, then the arguments split on quotes",
            args.SequenceEqual(new[] { "run", "/tmp/game/r5apex.exe", "-nodiscord" }),
            string.Join(" ", args));

        // Read once and compare the text, so the failure line is the same string the
        // assertion compared rather than a second lookup that could answer differently.
        var dataPath = info.Environment.TryGetValue("STEAM_COMPAT_DATA_PATH", out var found) && found is not null
            ? found : "(unset)";
        check("launch: STEAM_COMPAT_DATA_PATH is the prefix",
            dataPath == Path.GetFullPath(prefix), dataPath);

        check("launch: the launcher's own contract is set",
            info.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] == ProtonLauncher.SteamRoot()
            && info.Environment["VPROJECT"] == "1"
            && info.Environment["FROM_R5F_LAUNCHER"] == "1");

        // The Wine-era launcher set WINEPREFIX/WINEARCH/WINEDLLOVERRIDES because it
        // was starting Wine directly. Proton's script sets all three itself, and a
        // WINEPREFIX pointing at a compatdata root would be a prefix Proton does not
        // recognise — so their absence is the assertion, not an oversight.
        check("launch: no Wine-era variables are set by the launcher",
            !info.Environment.ContainsKey("WINEPREFIX")
            && !info.Environment.ContainsKey("WINEARCH")
            && !info.Environment.ContainsKey("WINEDLLOVERRIDES"));

        var quiet = ProtonLauncher.BuildStartInfo(
            new ProtonLauncher.ProtonRunOptions(protonDir, prefix, "/tmp/game/r5apex.exe",
                SetFromLauncher: false),
            capture: false);
        check("launch: FROM_R5F_LAUNCHER is opt-out, and the streams follow `capture`",
            !quiet.Environment.ContainsKey("FROM_R5F_LAUNCHER")
            && !quiet.RedirectStandardOutput && !quiet.RedirectStandardError,
            string.Join(",", quiet.Environment.Keys.Where(k => k.StartsWith("FROM", StringComparison.Ordinal))));

        var extra = ProtonLauncher.BuildStartInfo(
            new ProtonLauncher.ProtonRunOptions(protonDir, prefix, "/tmp/game/r5apex.exe",
                ExtraEnv: new Dictionary<string, string> { ["R5F_HOSTED_CONSOLE"] = "1" }),
            capture: false);
        check("launch: extra environment is merged over the base, which is how a launch is extended",
            extra.Environment["R5F_HOSTED_CONSOLE"] == "1"
            && extra.Environment["VPROJECT"] == "1");

        check("launch: the description names the Proton build and the prefix",
            ProtonLauncher.Describe(new ProtonLauncher.ProtonRunOptions(protonDir, prefix, "x"))
                is { } d && d.Contains("proton-build") && d.Contains(prefix),
            ProtonLauncher.Describe(new ProtonLauncher.ProtonRunOptions(protonDir, prefix, "x")));
    }

    // -------------------------------------------------------------------- bridge

    /// <summary>
    /// The bridge, the game's own identity verdict, and the Wine this launcher is
    /// still allowed to reach.
    ///
    /// The classifier is pure, so one fixture per literal pins it. The gate is
    /// exercised only through the paths that start nothing: no EA App, and a dry run
    /// with no <c>start</c> delegate (which is what the <c>--ea-probe</c> lane and
    /// this suite both pass, because a probe must never start EA). Nothing here binds
    /// 3216 or 3215, and the two fixtures under it are empty files with Wine's names
    /// — no Wine is executed, so nothing touches <c>~/Games/r5flowstate</c>.
    ///
    /// The install shim and the registry import that used to be asserted here are
    /// gone with the split runtime they existed for: they copied an EA install from
    /// one prefix into another, and there is one prefix now.
    /// </summary>
    static void BridgeChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        // --- the classifier: one line in, one verdict out ---
        var fixtures = new (string Line, PlatformIdentityVerdict Want)[]
        {
            ("[NET-OBS] HANDSHAKE: using nucleusId=1000421337 persona='player' (Origin) password=0",
                PlatformIdentityVerdict.HandshakeUsedIdentity),
            ("[EbisuSDK] platform token arrived after 0.6s (512 bytes)", PlatformIdentityVerdict.Ready),
            ("[EbisuSDK] platform identity enabled; signed token will be requested",
                PlatformIdentityVerdict.Waiting),
            ("[EbisuSDK] platform token never arrived (30s) -- this client cannot prove its account",
                PlatformIdentityVerdict.Failed),
            ("[AUTH] Origin identity never arrived; connect to '203.0.113.7:37015' dropped",
                PlatformIdentityVerdict.Failed),
            ("[EbisuSDK] no platform token expected on this client",
                PlatformIdentityVerdict.NoTokenExpected),
            ("[NET-OBS] HANDSHAKE hold: Origin identity not ready", PlatformIdentityVerdict.Waiting),
            ("[JOIN-AUTH] holding connect to '203.0.113.7:37015' (no identity)",
                PlatformIdentityVerdict.Waiting),
            ("[JOIN-AUTH] loopback connect proceeding without token",
                PlatformIdentityVerdict.LoopbackNoToken),
            ("[OFFLINE-GUARD] patch D pattern unresolved - EA App install check still fatal",
                PlatformIdentityVerdict.InstallCheckFatal),
            ("[OFFLINE-GUARD] patch E - running EA App still handshakes",
                PlatformIdentityVerdict.HandshakeGuardInactive),
            ("[OFFLINE-GUARD] platform client launch BLOCKED (A): EADesktop",
                PlatformIdentityVerdict.LaunchBlocked),
        };

        var wrong = fixtures.FirstOrDefault(f => PlatformIdentityLines.Classify(f.Line) != f.Want);
        check($"identity classifier: {fixtures.Length} literals, one verdict each",
            wrong.Line is null,
            wrong.Line is null ? $"{fixtures.Length} fixtures"
                               : $"'{wrong.Line}' -> {PlatformIdentityLines.Classify(wrong.Line)}");

        check("identity classifier: a line that says nothing about identity is None",
            PlatformIdentityLines.Classify("Loading map mp_lobby") == PlatformIdentityVerdict.None
            && PlatformIdentityLines.Classify("") == PlatformIdentityVerdict.None
            && PlatformIdentityLines.Classify(null) == PlatformIdentityVerdict.None
            // The install check is matched on two fragments, so half of the pair is
            // not the fatal verdict — otherwise every OFFLINE-GUARD line would read
            // as "the game cannot see your EA install".
            && PlatformIdentityLines.Classify("[OFFLINE-GUARD] patch A applied")
               == PlatformIdentityVerdict.None);

        check("identity verdicts: only a failed identity and a failed install check are failures",
            PlatformIdentityLines.IsFailure(PlatformIdentityVerdict.Failed)
            && PlatformIdentityLines.IsFailure(PlatformIdentityVerdict.InstallCheckFatal)
            && !PlatformIdentityLines.IsFailure(PlatformIdentityVerdict.Waiting)
            && !PlatformIdentityLines.IsFailure(PlatformIdentityVerdict.NoTokenExpected)
            && !PlatformIdentityLines.IsFailure(PlatformIdentityVerdict.LoopbackNoToken)
            && !PlatformIdentityLines.IsFailure(PlatformIdentityVerdict.None)
            && PlatformIdentityLines.IsSuccess(PlatformIdentityVerdict.HandshakeUsedIdentity)
            && PlatformIdentityLines.IsSuccess(PlatformIdentityVerdict.Ready)
            && !PlatformIdentityLines.IsSuccess(PlatformIdentityVerdict.Waiting));

        // Every failure names the way out. The way out used to be a setting — the
        // two sentences said "switch EA Runtime to Proton" and "press Bridge EA
        // install" — and both of those are deleted, so this is the check that would
        // catch a removal leaving the player a dead join and an instruction for a
        // switch that is not on the window any more.
        check("identity verdicts: a failure says what to do, and names no removed setting",
            PlatformIdentityLines.Describe(PlatformIdentityVerdict.Failed).Contains("signed in")
            && PlatformIdentityLines.Describe(PlatformIdentityVerdict.InstallCheckFatal)
                .Contains("Settings")
            && PlatformIdentityLines.Describe(PlatformIdentityVerdict.None).Length == 0);

        check("identity verdicts: no sentence sends the player to a runtime switch or a bridge",
            Enum.GetValues<PlatformIdentityVerdict>().All(v =>
                !PlatformIdentityLines.Describe(v).Contains("EA Runtime", StringComparison.Ordinal)
                && !PlatformIdentityLines.Describe(v).Contains("Bridge", StringComparison.Ordinal)));

        check("identity verdicts: every verdict has a sentence except None",
            Enum.GetValues<PlatformIdentityVerdict>()
                .All(v => (v == PlatformIdentityVerdict.None)
                          == (PlatformIdentityLines.Describe(v).Length == 0)));

        // --- the gate: the two answers that start nothing ---
        var listening = EaChannelProbe.IsListening(EaChannelProbe.Probe());
        var missing = EaBridge.EnsureAsync(null, null).GetAwaiter().GetResult();
        check("bridge: no EA App is a refusal, not a launch",
            listening || (missing.State == EaBridgeState.EaMissing && !missing.CanJoin
                          && missing.Line.Contains("install it")),
            listening ? "skipped: something is listening on 3216" : missing.Line);

        var dry = EaBridge.EnsureAsync("/tmp/ea/EADesktop.exe", null).GetAwaiter().GetResult();
        check("bridge: a dry run never starts anything and reports the closed port",
            listening || (dry.State == EaBridgeState.Refused && !dry.CanJoin
                          && dry.OwnedByLauncher == false && dry.Pid is null),
            listening ? "skipped: something is listening on 3216" : dry.Line);

        check("bridge: every state has a word, and they differ",
            Enum.GetValues<EaBridgeState>().Select(EaBridge.Describe).Distinct().Count()
                == Enum.GetValues<EaBridgeState>().Length
            && Enum.GetValues<EaBridgeState>().All(s => EaBridge.Describe(s).Length > 0));

        // --- the watcher: only a child the launcher started is restarted ---
        var starts = 0;
        System.Diagnostics.Process Start() { starts++; return System.Diagnostics.Process.GetCurrentProcess(); }

        var notOurs = new EaBridgeWatcher(
            new EaBridgeReport(EaBridgeState.Listening, true, "already listening"), Start,
            deathGrace: TimeSpan.Zero);
        notOurs.Poll();
        var afterBlip = notOurs.Poll();
        notOurs.Poll();
        check("bridge watcher: a channel the launcher does not own is reported, never restarted",
            listening || (notOurs.Dead && starts == 0 && notOurs.LastLine is not null),
            listening ? "skipped: something is listening on 3216"
                      : $"{afterBlip}, {starts} restart(s)");

        var ours = new EaBridgeWatcher(
            new EaBridgeReport(EaBridgeState.Listening, true, "started by the launcher", true),
            Start, deathGrace: TimeSpan.Zero);
        ours.Poll();                                  // a blip: EA restarts its listener
        var relaunching = ours.Poll();                // one restart, and only one
        var afterRestart = ours.Poll();               // the restarted EA gets its own grace
        ours.Poll();                                  // gone again: final
        check("bridge watcher: EA that the launcher started is relaunched once, then given up on",
            listening || (starts == 1 && ours.Dead
                          && relaunching == EaBridgeState.Starting
                          && afterRestart == EaBridgeState.Listening),
            listening ? "skipped: something is listening on 3216" : $"{starts} restart(s)");

        // --- Proton's own Wine, which is the only Wine this launcher may touch ---
        var protonDir = Path.Combine(root, "proton-build");
        Directory.CreateDirectory(Path.Combine(protonDir, "files", "bin"));
        File.WriteAllText(Path.Combine(protonDir, "files", "bin", "wine"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(protonDir, "files", "bin", "wineserver"), "#!/bin/sh\n");

        check("proton wine: it is the Wine inside the Proton build, and nothing else",
            ProtonWine.ProtonWineBinary(protonDir)
                == Path.Combine(protonDir, "files", "bin", "wine")
            && ProtonWine.ProtonWineBinary(Path.Combine(root, "not-a-proton-build")) is null
            && ProtonWine.ProtonWineBinary("") is null,
            ProtonWine.ProtonWineBinary(protonDir) ?? "none");

        // The wineserver must be the sibling of that wine rather than whatever is on
        // PATH: a system wineserver driving a Proton prefix is the unsupported shape
        // the prep avoids, and it is exactly what a PATH lookup would hand back.
        var tools = ProtonWine.ProtonWineToolchain.ForProton(protonDir);
        check("proton wine: the toolchain is the two binaries side by side",
            tools is not null
            && tools.Wine == Path.Combine(protonDir, "files", "bin", "wine")
            && tools.WineServer == Path.Combine(protonDir, "files", "bin", "wineserver"),
            tools?.Describe() ?? "none");

        var halfBuild = Path.Combine(root, "proton-without-server");
        Directory.CreateDirectory(Path.Combine(halfBuild, "files", "bin"));
        File.WriteAllText(Path.Combine(halfBuild, "files", "bin", "wine"), "#!/bin/sh\n");
        check("proton wine: a build with a wine but no wineserver has no toolchain, not a borrowed one",
            ProtonWine.ProtonWineToolchain.ForProton(halfBuild) is null
            && ProtonWine.ProtonWineToolchain.ForProton(Path.Combine(root, "not-a-build")) is null
            // dist/bin/wine is the other place a Proton build keeps it, and a build
            // that only has that one still counts.
            && MakeDistWine(root) is { } dist
            && ProtonWine.ProtonWineBinary(dist) == Path.Combine(dist, "dist", "bin", "wine"),
            ProtonWine.ProtonWineBinary(Path.Combine(root, "not-a-build")) ?? "none");

        // IsPrefixInUse reads /proc: only a Wine process holds a prefix open, and the
        // answer for one nobody has open is false. This is the gate that stops the
        // prep from writing a registry a live wineserver would write back.
        check("prefix in use: a prefix no process names is not in use",
            !ProtonWine.IsPrefixInUse(Path.Combine(root, "nobody-has-this-open"))
            && !ProtonWine.IsPrefixInUse("")
            // This suite's own temp root: false whatever else is running on the box,
            // because the suite never starts Wine.
            && !ProtonWine.IsPrefixInUse(root));
    }

    /// <summary>A Proton build that keeps its Wine under <c>dist/bin</c> instead —
    /// the older layout, and the second place <see cref="ProtonWine.ProtonWineBinary"/>
    /// looks.</summary>
    static string? MakeDistWine(string root)
    {
        var dir = Path.Combine(root, "dist-proton");
        Directory.CreateDirectory(Path.Combine(dir, "dist", "bin"));
        File.WriteAllText(Path.Combine(dir, "dist", "bin", "wine"), "#!/bin/sh\n");
        return dir;
    }

    // --------------------------------------------------------------- the runtimes

    /// <summary>
    /// The EA App's redistributables — the prep that runs from the Install press and
    /// from the Prepare button, in the one prefix there is.
    ///
    /// Hermetic: winetricks is never executed. Every prefix here is a directory tree
    /// the suite writes itself, the Proton build is a fixture with a wine and a
    /// wineserver in it (two empty files), and the only calls that could run anything
    /// are made with a build that has no Wine in it — which is the refusal being
    /// asserted. Nothing near <c>~/Games/r5flowstate</c>.
    /// </summary>
    static void PrepChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        const string win10Keys =
            "[Software\\\\Microsoft\\\\Windows NT\\\\CurrentVersion] 1\n"
            + "\"ProductName\"=\"Windows 10 Pro\"\n"
            + "\"CurrentVersion\"=\"6.3\"\n"
            + "\"CurrentBuild\"=\"19045\"\n";
        const string win7Keys =
            "[Software\\\\Microsoft\\\\Windows NT\\\\CurrentVersion] 1\n"
            + "\"ProductName\"=\"Windows 7 Ultimate\"\n"
            + "\"CurrentVersion\"=\"6.1\"\n";

        // A prefix as Proton leaves one: drive_c under pfx/, and the marker that says
        // Proton built it.
        string MakePrefix(string name, string keys, bool bootstrapped = true)
        {
            var prefix = Path.Combine(root, name);
            var pfx = Path.Combine(prefix, "pfx");
            Directory.CreateDirectory(Path.Combine(pfx, "drive_c", "windows", "Fonts"));
            Directory.CreateDirectory(Path.Combine(pfx, "drive_c", "windows", "system32"));
            if (bootstrapped)
            {
                File.WriteAllText(Path.Combine(pfx, "system.reg"), "WINE REGISTRY Version 2\n\n" + keys);
                File.WriteAllText(Path.Combine(pfx, "creation_sync_guard"), "");
            }
            return prefix;
        }

        // A Proton build as Proton ships one: the two binaries side by side.
        string MakeProton(string name, bool withServer = true)
        {
            var dir = Path.Combine(root, name);
            var bin = Path.Combine(dir, "files", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "wine"), "");
            if (withServer)
                File.WriteAllText(Path.Combine(bin, "wineserver"), "");
            return dir;
        }

        var proton = MakeProton("proton-build");
        var noWine = Path.Combine(root, "not-a-build");

        // --- what the probe reads ---
        var bare = MakePrefix("bare", win10Keys);
        var bareFound = EaPrefixPrep.Probe(bare);
        check("prep: a fresh Proton prefix reads as fonts and runtimes missing, version set",
            !bareFound.Complete && !bareFound.CoreFonts && !bareFound.VcRuntime && bareFound.Windows10,
            bareFound.Describe());

        var win7 = MakePrefix("win7", win7Keys);
        check("prep: the Windows version comes out of system.reg, both ways",
            !EaPrefixPrep.Probe(win7).Windows10
            && EaPrefixPrep.Probe(bare).Windows10
            && !EaPrefixPrep.Probe(MakePrefix("no-hive", win10Keys, bootstrapped: false)).Windows10);

        // Proton ships Arial and the VC++ DLLs as symlinks into its own files/share,
        // so a present file proves nothing on its own. This is the check that keeps
        // the probe from reading Proton's own baseline as somebody's install.
        var outside = Path.Combine(root, "proton-share");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "mfc140.dll"), "not really mfc");
        File.WriteAllText(Path.Combine(outside, "arial.ttf"), "not really arial");
        var symlinked = MakePrefix("symlinked", win10Keys);
        var symlinkedPfx = Path.Combine(symlinked, "pfx", "drive_c", "windows");
        File.CreateSymbolicLink(Path.Combine(symlinkedPfx, "system32", "mfc140.dll"),
            Path.Combine(outside, "mfc140.dll"));
        File.CreateSymbolicLink(Path.Combine(symlinkedPfx, "Fonts", "arial.ttf"),
            Path.Combine(outside, "arial.ttf"));
        var symlinkFound = EaPrefixPrep.Probe(symlinked);
        check("prep: a Wine builtin symlinked into the prefix is not an install",
            !symlinkFound.VcRuntime && !symlinkFound.CoreFonts
            && EaPrefixPrep.Missing(symlinkFound).Count == 2,
            symlinkFound.Describe());

        // The real thing: winetricks' own markers, as real files. The symlink has to go
        // first — writing to that path would write through it and leave it a symlink,
        // which is the mistake this whole probe is about.
        File.Delete(Path.Combine(symlinkedPfx, "system32", "mfc140.dll"));
        File.WriteAllText(Path.Combine(symlinkedPfx, "system32", "mfc140.dll"), "real");
        File.WriteAllText(Path.Combine(symlinkedPfx, "Fonts", "corefonts.installed"), "");
        var installed = EaPrefixPrep.Probe(symlinked);
        check("prep: winetricks' own markers are the evidence, and both of them are read",
            installed.Complete && installed.CoreFonts && installed.VcRuntime
            && EaPrefixPrep.Missing(installed).Count == 0,
            installed.Describe());

        check("prep: the verbs to run are the ones missing, in the order they run",
            EaPrefixPrep.Missing(bareFound).SequenceEqual(
                new[] { EaPrefixPrep.CoreFontsVerb, EaPrefixPrep.VcRunVerb })
            && EaPrefixPrep.Missing(new EaPrefixComponents(true, true, false))
                .SequenceEqual(new[] { EaPrefixPrep.Win10Verb })
            && EaPrefixPrep.Missing(installed).Count == 0
            && EaPrefixPrep.Verbs.Count == 3,
            string.Join(",", EaPrefixPrep.Missing(bareFound)));

        // --- the refusals ---
        check("prep: a Proton build with no Wine in it is refused, and says so",
            EaPrefixPrep.Refusal(bare, noWine) is { } r && r.Contains("no Wine inside the Proton build"),
            EaPrefixPrep.Refusal(bare, noWine) ?? "(none)");

        var noGuard = MakePrefix("no-guard", win10Keys);
        File.Delete(Path.Combine(noGuard, "pfx", "creation_sync_guard"));
        check("prep: a prefix Proton did not build is refused by name",
            EaPrefixPrep.Refusal(noGuard, proton) is { } g && g.Contains("creation_sync_guard"),
            EaPrefixPrep.Refusal(noGuard, proton) ?? "(none)");

        var empty = Path.Combine(root, "empty-prefix");
        Directory.CreateDirectory(empty);
        check("prep: a prefix that does not exist yet is refused, and says what creates it",
            EaPrefixPrep.Refusal(empty, proton) is { } p
            && p.Contains("does not exist yet") && p.Contains("again"),
            EaPrefixPrep.Refusal(empty, proton) ?? "(none)");

        check("prep: no prefix at all is refused first, before anything is looked at",
            EaPrefixPrep.Refusal("", "") is { } n && n.Contains("no prefix"),
            EaPrefixPrep.Refusal("", "") ?? "(none)");

        // A prefix something is running in. The suite cannot start a Wine to make this
        // true, so it is asserted the other way round — that the check's own answer for
        // a prefix nobody has open is the one that lets the prep through.
        check("prep: a prefix nothing is running in is not refused for being in use",
            EaPrefixPrep.Refusal(bare, proton) is null
            || !EaPrefixPrep.Refusal(bare, proton)!.Contains("in use"),
            EaPrefixPrep.Refusal(bare, proton) ?? "(none)");

        // --- the run itself ---
        var prepared = EaPrefixPrep.RunAsync(symlinked, proton).GetAwaiter().GetResult();
        check("prep: an already-prepared prefix is a no-op, not a second download",
            prepared.Ok && prepared.Summary.Contains("nothing to do")
            && prepared.Lines.Any(l => l.Contains("already has everything")),
            prepared.Summary);

        var refused = EaPrefixPrep.RunAsync(bare, noWine).GetAwaiter().GetResult();
        check("prep: a run that cannot start reports the refusal and leaves the prefix alone",
            !refused.Ok && refused.Summary.Contains("no Wine inside the Proton build")
            && refused.Lines.Count == 0
            && !File.Exists(Path.Combine(bare, "pfx", "drive_c", "windows", "Fonts",
                "corefonts.installed")),
            refused.Summary);

        // --- what winetricks would actually be handed ---
        var tools = ProtonWine.ProtonWineToolchain.ForProton(proton)!;
        var args = ProtonWine.WinetricksArgs(new[] { EaPrefixPrep.CoreFontsVerb });
        check("prep: winetricks is asked quietly and never forced",
            args.SequenceEqual(new[] { "-q", "corefonts" }) && !args.Contains("-f"),
            string.Join(" ", args));

        var psi = ProtonWine.BuildWinetricksStartInfo("/usr/bin/winetricks",
            Path.Combine(bare, "pfx"), tools, new[] { EaPrefixPrep.CoreFontsVerb, EaPrefixPrep.VcRunVerb });
        psi.Environment.TryGetValue("WINEDLLOVERRIDES", out var overrides);
        check("prep: the run gets that prefix, that Wine, and no launching overrides",
            psi.FileName == "/usr/bin/winetricks"
            && psi.ArgumentList.SequenceEqual(new[] { "-q", "corefonts", "vcrun2019" })
            && psi.Environment["WINEPREFIX"] == Path.GetFullPath(Path.Combine(bare, "pfx"))
            && psi.Environment["WINE"] == tools.Wine
            && psi.Environment["WINESERVER"] == tools.WineServer
            && psi.Environment["WINEARCH"] == "win64"
            && overrides != ProtonWine.DefaultDllOverrides,
            $"{psi.Environment["WINEPREFIX"]} / {psi.Environment["WINE"]} / {overrides ?? "(unset)"}");
    }

    // ------------------------------------------------------------ the art cache

    /// <summary>
    /// The file the hosted decoder writes and the launcher reads.
    ///
    /// <para>Worth its own group because it is the one thing standing between a
    /// decoded picture and a blank pane, and because nothing here can be tested by
    /// looking: the launcher never decodes a retail loadscreen itself, so the only
    /// thing it can be wrong about is this format.</para>
    /// </summary>
    static void ArtCacheChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        Directory.CreateDirectory(root);

        var pixels = new LoadscreenPixels
        {
            Width = 4,
            Height = 2,
            SourceWidth = 1920,
            SourcePath = @"Z:\games\x_loadscreen.rpak",
            Bgra = new byte[4 * 2 * 4],
        };
        for (var i = 0; i < pixels.Bgra.Length; i++)
            pixels.Bgra[i] = (byte)(i * 7 % 251);

        var path = Path.Combine(root, ArtCache.FileNameFor(ArtCache.KeyForStem("MP_RR_Test")));
        var wrote = ArtCache.Write(path, pixels, out var writeError);

        string? readError = null;
        check("art cache: a decode round-trips through the file, pixels included",
            wrote && writeError is null
            && ArtCache.TryRead(path, out var back, out readError) && back is not null
            && back.Width == 4 && back.Height == 2 && back.SourceWidth == 1920
            && back.Bgra.SequenceEqual(pixels.Bgra),
            readError ?? writeError ?? path);

        check("art cache: a half-written file is never what a reader sees",
            Directory.GetFiles(root, "*.part").Length == 0,
            string.Join(", ", Directory.GetFiles(root).Select(Path.GetFileName)));

        // The header read is the "is this already decoded?" answer, and it has to be
        // answerable without pulling a two-megabyte picture off the disk.
        check("art cache: the header answers on its own, and agrees with the full read",
            ArtCache.TryReadHeader(path, out var header, out var headerError) && headerError is null
            && header.Width == 4 && header.Height == 2 && header.SourceWidth == 1920
            && header.Payload == 32 && header.Version == ArtCache.Version,
            headerError ?? $"{header.Width}x{header.Height} payload {header.Payload}");

        // The header check validates the length too — a file that claims pixels it
        // does not hold is refused by both readers, and that is the point: a header
        // read that trusted its own arithmetic would hand the pane a short buffer.
        // (It was written the other way round first: "a header with no pixels is
        // cached but unreadable" assumed the cheap check skipped the length test.
        // It does not, and the cheaper check is the better one for it.)
        var headOnly = Path.Combine(root, "head-only.r5fa");
        File.Copy(path, headOnly, overwrite: true);
        using (var fs = new FileStream(headOnly, FileMode.Open, FileAccess.Write)) fs.SetLength(ArtCache.HeaderSize);
        string? shortRead = null;
        check("art cache: a header claiming pixels the file does not hold is refused by both readers",
            !ArtCache.TryReadHeader(headOnly, out _, out var shortHeader)
            && !ArtCache.TryRead(headOnly, out _, out shortRead)
            && shortHeader is not null && shortRead is not null,
            shortRead ?? shortHeader ?? "(both readers accepted a file with no pixels)");

        var almost = Path.Combine(root, "almost.r5fa");
        File.Copy(path, almost, overwrite: true);
        using (var fs = new FileStream(almost, FileMode.Open, FileAccess.Write))
            fs.SetLength(ArtCache.HeaderSize + 8);
        check("art cache: a file cut off mid-picture is refused, not read as a shorter one",
            !ArtCache.TryReadHeader(almost, out _, out _) && !ArtCache.TryRead(almost, out _, out _),
            "a partial payload was accepted");

        check("art cache: the key folds case, so one map is one file",
            ArtCache.KeyForStem("MP_RR_Test") == ArtCache.KeyForStem("mp_rr_test")
            && path == Path.Combine(root, ArtCache.FileNameFor(ArtCache.KeyForStem("mp_rr_test"))),
            ArtCache.KeyForStem("MP_RR_Test"));

        // The rule that matters for a pak is that the same pak is one file however
        // it was reached. The two sides resolve it on their own platform — the
        // launcher from a Linux path, the host from the Z: path it was handed — and
        // both land on the basename, which is what this asserts.
        check("art cache: one art pak is one file, by name and not by folder",
            ArtCache.KeyForPak("/one/place/loadscreen_x.rpak")
            == ArtCache.KeyForPak("/another/place/loadscreen_x.rpak")
            && ArtCache.KeyForPak("/p/loadscreen_x.rpak") == "loadscreen_x",
            ArtCache.KeyForPak("/one/place/loadscreen_x.rpak"));

        check("art cache: a stem cannot name its way out of the cache directory",
            ArtCache.KeyForStem("../../etc/passwd") is { } escape
            && !escape.Contains('/') && !escape.Contains('\\') && !escape.StartsWith('.')
            && ArtCache.KeyForStem("..") == "unnamed"
            && ArtCache.KeyForStem("") == "unnamed"
            && ArtCache.KeyForStem("   ") == "unnamed",
            ArtCache.KeyForStem("../../etc/passwd"));

        // Three ways a cache file goes bad, and each has to be refused rather than
        // read as pixels: a reader that trusts a header is a reader that draws noise.
        check("art cache: a truncated file, a foreign file and a future version are all refused",
            Refuse(root, "truncated.r5fa", path, fs => fs.SetLength(10), "truncated")
            && Refuse(root, "foreign.r5fa", path, fs => fs.Write(new byte[] { 1, 2, 3, 4 }, 0, 4), "not R5F art")
            && Refuse(root, "future.r5fa", path, fs =>
            {
                fs.Position = 4;
                fs.Write(BitConverter.GetBytes(ArtCache.Version + 1), 0, 4);
            }, "version"),
            "one of the three was accepted");

        check("art cache: a missing file is a reason, not an exception",
            !ArtCache.TryRead(Path.Combine(root, "nothing-here.r5fa"), out _, out var absent)
            && absent is not null
            && !ArtCache.TryReadHeader("", out _, out _)
            && !ArtCache.TryReadHeader(Path.Combine(root, "nothing-here.r5fa"), out _, out _),
            absent ?? "(no reason given)");
    }

    /// <summary>Corrupt a copy of a good cache file one way, and report whether the
    /// reader refused it for the reason it should have.</summary>
    static bool Refuse(string root, string name, string good, Action<FileStream> damage, string expected)
    {
        var path = Path.Combine(root, name);
        File.Copy(good, path, overwrite: true);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            damage(fs);

        return !ArtCache.TryRead(path, out _, out var error)
               && error is not null
               && error.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------- the decode

    /// <summary>
    /// The Proton side of the art lane: where it puts things, and what it makes of
    /// the host's records.
    ///
    /// <para>The records half is the point. It is the only part of the lane with no
    /// process and no prefix in it, and the first real run of
    /// <c>--art-probe</c> found a bug in exactly it: the host echoes its own argv,
    /// so a pak comes back as the <c>Z:</c> path it was handed over as, and a
    /// lookup by the Linux path it came from found nothing while the decoded file
    /// sat in the cache. That is the failure this asserts against.</para>
    /// </summary>
    static void ArtDecodeChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        Directory.CreateDirectory(root);

        check("art: the cache is one folder per width, so a tile and a hero cannot collide",
            ArtDecode.CacheDir(196) != ArtDecode.CacheDir(960)
            && ArtDecode.CacheDir(196).EndsWith("w196", StringComparison.Ordinal)
            && ArtDecode.CacheDir(960).StartsWith(ArtDecode.CacheRoot(), StringComparison.Ordinal),
            $"{ArtDecode.CacheDir(196)} vs {ArtDecode.CacheDir(960)}");

        check("art: a map's cache file is its folded stem under that width",
            ArtDecode.CacheFileForStem(960, "MP_RR_X")
            == Path.Combine(ArtDecode.CacheDir(960), "mp_rr_x.r5fa")
            && ArtDecode.CacheFileForPak(960, "/p/loadscreen_a.rpak")
            == Path.Combine(ArtDecode.CacheDir(960), "loadscreen_a.r5fa"),
            ArtDecode.CacheFileForStem(960, "MP_RR_X"));

        check("art: the decoder gets a prefix of its own, never the game's",
            ArtDecode.CompatDataDir().EndsWith("r5flowstate/art-prefix", StringComparison.Ordinal)
            && !ArtDecode.CompatDataDir().StartsWith(LinuxSettings.DefaultPrefixPath, StringComparison.Ordinal)
            && ArtDecode.HostDir().EndsWith("winhost", StringComparison.Ordinal)
            && (ArtDecode.FindHost() is null
                || Path.GetFileName(ArtDecode.FindHost()) == ArtDecode.HostExeName),
            $"{ArtDecode.CompatDataDir()} / {ArtDecode.FindHost() ?? "no host"}");

        var records = Path.Combine(root, "records.tsv");
        var pakAsked = "/home/p/Downloads/flowstate/paks/Win64/loadscreen_a.rpak";
        File.WriteAllLines(records, new[]
        {
            "host\t2.0.0.0",
            @"ok" + "\t" + "stem\tmp_rr_x\tmp_rr_x.r5fa\t960\t538\t1915\t" + @"Z:\games\x_loadscreen.rpak",
            "err\tstem\tmp_lobby\tmp_lobby_loadscreen.rpak: empty pak",
            "ok\tpak\t" + PrefixLayout.ToZDrive(pakAsked) + "\tloadscreen_a.r5fa\t960\t540\t1920\t"
                + PrefixLayout.ToZDrive(pakAsked),
            "warn\ttoolde\tno oo2core beside the host",
            "done\t2\t1",
        });

        var parsed = ArtDecode.Parse(records, 960,
            new[] { "mp_rr_x", "mp_lobby", "mp_never" }, new[] { pakAsked });

        check("art records: every request gets an answer, in the order it was asked",
            parsed.Count == 4
            && parsed[0].Input == "mp_rr_x" && parsed[1].Input == "mp_lobby"
            && parsed[2].Input == "mp_never" && parsed[3].Input == pakAsked,
            string.Join(", ", parsed.Select(o => o.Input)));

        check("art records: an ok record resolves to the cache file under this width",
            parsed[0].Ok
            && parsed[0].CachePath == Path.Combine(ArtDecode.CacheDir(960), "mp_rr_x.r5fa")
            && parsed[0].Source == @"Z:\games\x_loadscreen.rpak",
            $"{parsed[0].CachePath} from {parsed[0].Source}");

        // The bug this group exists for: the host echoes the Z: path back, so the
        // outcome has to be matched on that and reported against the path asked for.
        check("art records: a pak echoed back as a Z: path still finds its cache file",
            parsed[3].Ok
            && parsed[3].CachePath == Path.Combine(ArtDecode.CacheDir(960), "loadscreen_a.r5fa"),
            parsed[3].Ok ? parsed[3].CachePath : parsed[3].Reason);

        check("art records: the decoder's own reason survives to the caller",
            !parsed[1].Ok && parsed[1].Reason == "mp_lobby_loadscreen.rpak: empty pak",
            parsed[1].Reason);

        check("art records: a request the host never reported says so",
            !parsed[2].Ok && parsed[2].Reason.Contains("did not report", StringComparison.Ordinal)
            && !parsed[2].Ok,
            parsed[2].Reason);

        // And the other cause of a missing record, which is not the host's fault at
        // all: a batch the caller replaced a moment after starting it, whose records
        // file has no line in it yet because the host has not written one. Reading
        // that as the host's silence is what filled the player's log with "the art
        // host did not report this one" for pictures that were busy decoding — 59 of
        // them in one session, every one of them preceded by the launcher's own
        // "decode was cancelled" line for the same batch.
        var replaced = ArtDecode.Parse(records, 960, new[] { "mp_never" }, Array.Empty<string>(),
            ArtDecode.ReplacedReason);
        check("art records: a batch that was replaced blames the replacement, not the host",
            replaced is { Count: 1 } && !replaced[0].Ok
            && replaced[0].Reason == ArtDecode.ReplacedReason
            && ArtDecode.ReplacedReason != ArtDecode.UnreportedReason
            && ArtDecode.ReplacedNote != ArtDecode.UnreportedReason,
            replaced[0].Reason);

        // The default stays what it was: a caller that passes no reason still gets
        // the host's-silence answer, so this reads as an addition and not a swap.
        check("art records: the silence answer is still the default",
            ArtDecode.Parse(records, 960, new[] { "mp_never" }, Array.Empty<string>())[0].Reason
                == ArtDecode.UnreportedReason
            && ArtDecode.UnreportedReason == "the art host did not report this one",
            ArtDecode.UnreportedReason);

        // Asking for something already abandoned must not start Proton: the request
        // that replaced it is what the prefix is for. Decided before the host and
        // Proton lookups, which is why the note is this one and not "no Proton build".
        using (var abandoned = new CancellationTokenSource())
        {
            abandoned.Cancel();
            var gone = ArtDecode.FetchAsync(
                new ArtRequest("", "/i", 960, new[] { "mp_rr_x" }), abandoned.Token)
                .GetAwaiter().GetResult();
            check("art: a request already abandoned is a note and not a spawn",
                !gone.Ran && gone.Outcomes.Count == 0 && gone.Note == ArtDecode.ReplacedNote,
                gone.Note);
        }

        // The third cause of a missing answer, and the one the player's second
        // report turned out to be: a host that never wrote a single record. Not the
        // same thing as silence about one picture, and saying so per item is what
        // made sixty identical lines out of one startup failure.
        var never = ArtDecode.Parse(Path.Combine(root, "never-ran.tsv"), 960,
            new[] { "mp_a", "mp_b" }, Array.Empty<string>(), ArtDecode.NotStartedReason);
        check("art records: a host that never wrote a record is not reported as per-item silence",
            never is { Count: 2 } && never.All(o => !o.Ok)
            && never.All(o => o.Reason == ArtDecode.NotStartedReason)
            && ArtDecode.NotStartedReason == "the art host did not start",
            never[0].Reason);

        check("art records: the three reasons a batch can come back short are three different ones",
            ArtDecode.UnreportedReason != ArtDecode.ReplacedReason
            && ArtDecode.UnreportedReason != ArtDecode.NotStartedReason
            && ArtDecode.ReplacedReason != ArtDecode.NotStartedReason,
            $"{ArtDecode.UnreportedReason} / {ArtDecode.ReplacedReason} / {ArtDecode.NotStartedReason}");

        check("art records: no records file at all is one unanswered line each, not a throw",
            ArtDecode.Parse(Path.Combine(root, "never-written.tsv"), 960, new[] { "a" }, Array.Empty<string>())
                is { Count: 1 } missing
            && !missing[0].Ok,
            "a missing records file read as an empty batch");

        check("art records: a line with no tabs is skipped instead of being read as a record",
            ArtDecode.Parse(Write(root, "junk.tsv", new[] { "", "not\tquite", "done" }), 960,
                new[] { "a" }, Array.Empty<string>()) is { Count: 1 } junk
            && !junk[0].Ok,
            "a malformed line became an outcome");

        // Nothing to do, and nothing to do it with: both are notes rather than
        // exceptions, because a launcher that cannot draw art still has to start.
        var empty = ArtDecode.EnsureCachedAsync(new ArtRequest("/p", "/i", 960)).GetAwaiter().GetResult();
        check("art: an empty request runs nothing and says so",
            !empty.Ran && empty.Outcomes.Count == 0 && empty.Note == "nothing asked for",
            empty.Note);

        var noProton = ArtDecode.FetchAsync(
            new ArtRequest("", "/i", 960, new[] { "mp_rr_x" })).GetAwaiter().GetResult();
        check("art: no Proton build is a note, and it is decided before anything is started",
            !noProton.Ran && noProton.ExitCode == 0 && noProton.Decoded == 0
            && noProton.Note.Contains("Proton", StringComparison.Ordinal),
            noProton.Note);

        // A Proton build that passes the existence check without being runnable, so
        // the install check is the one under test: the validations run host, then
        // Proton, then install, and each has to stop before anything is spawned —
        // which is what Ran == false says.
        var pretendProton = Path.Combine(root, "pretend-proton");
        Directory.CreateDirectory(pretendProton);
        File.WriteAllText(Path.Combine(pretendProton, "proton"), "");

        var noInstall = ArtDecode.FetchAsync(
            new ArtRequest(pretendProton, Path.Combine(root, "no-such-install"), 960,
                new[] { "mp_rr_x" })).GetAwaiter().GetResult();
        check("art: no install is a note, and it too is decided before anything is started",
            !noInstall.Ran && noInstall.ExitCode == 0 && noInstall.Decoded == 0
            && noInstall.Note.Contains("install", StringComparison.Ordinal),
            noInstall.Note);

        // The hot path, hermetically: a cache made here, so the player's is not
        // written to — which is what the cache-root override exists for. Everything
        // asked for is already decoded, so Proton must not be spawned at all.
        var real = Environment.GetEnvironmentVariable(ArtDecode.CacheRootEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ArtDecode.CacheRootEnvVar, root);
            var noInstallRoot = Path.Combine(root, "no-such-install");

            // Seeded at the tile width, not the hero's, so the cross-width answer
            // below is a real one and not "there is no Proton build" in disguise.
            var seed = ArtDecode.CacheFileForStem(196, "mp_rr_cached");
            Directory.CreateDirectory(ArtDecode.CacheDir(196));
            ArtCache.Write(seed, new LoadscreenPixels
            {
                Width = 196, Height = 110, SourceWidth = 1920,
                Bgra = new byte[196 * 110 * 4], SourcePath = "",
            }, out _);

            var warm = ArtDecode.EnsureCachedAsync(
                new ArtRequest(pretendProton, noInstallRoot, 196, new[] { "mp_rr_cached" }))
                .GetAwaiter().GetResult();

            check("art: a decoded map is served from the cache without spawning anything",
                !warm.Ran && warm.Note == "already decoded" && warm.Decoded == 1
                && warm.Outcomes[0].Ok
                && warm.Outcomes[0].CachePath == seed,
                warm.Note);

            // Same map, same cache root, other width: not a hit, so it goes on to
            // look for a host and an install — which is how a tile and a hero can
            // never be served each other's picture.
            var misWidth = ArtDecode.EnsureCachedAsync(
                new ArtRequest(pretendProton, noInstallRoot, 960, new[] { "mp_rr_cached" }))
                .GetAwaiter().GetResult();

            check("art: the same map at another width is not a cache hit",
                misWidth.Note != "already decoded" && misWidth.Decoded == 0
                && misWidth.Note.Contains("install", StringComparison.Ordinal),
                misWidth.Note);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArtDecode.CacheRootEnvVar, real);
        }
    }

    /// <summary>
    /// The locator both Windows helpers are found through, and the one thing about
    /// it that is load-bearing: each helper owns a folder named after itself under
    /// <c>winhost/</c>.
    ///
    /// <para>This is the second half of what the player reported. install.sh
    /// published the console relay into the art host's folder, on the theory that two
    /// win-x64 publishes of the same runtime produce the same framework files. They
    /// do not: both are <em>trimmed</em>, and the trimmer rewrites each app's copy of
    /// the framework it uses. The relay's <c>System.Console.dll</c> therefore landed
    /// on the host's, the host died at its first <c>Console.WriteLine</c>
    /// (<c>MissingMethodException</c>, exit 82) before writing one record, and every
    /// map came back as "the art host did not report this one" — sixty lines of it,
    /// with no decode attempted and no art anywhere. Reproduced on this box by
    /// publishing the relay over a working host folder, and fixed by giving each
    /// helper its own.</para>
    ///
    /// <para>Hermetic: the folder rule is pure, and where a helper is actually found
    /// is only *checked* against the shape it is allowed to have — never created or
    /// moved. install.sh's side of the same rule is read from source by
    /// <c>SelfTestRunner</c>, which is where the mistake was made.</para>
    /// </summary>
    static void WinHostChecks(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var hostFolder = WinHost.FolderFor(ArtDecode.HostExeName);
        var relayFolder = WinHost.FolderFor(HostedConsole.RelayExeName);

        check("winhost: each helper owns a folder named after it, and the two differ",
            hostFolder == "r5f-arthost" && relayFolder == "r5f-relay" && hostFolder != relayFolder,
            $"{ArtDecode.HostExeName} -> {hostFolder}/, {HostedConsole.RelayExeName} -> {relayFolder}/");

        check("winhost: the helpers live under winhost/, beside the launcher",
            WinHost.Dir().EndsWith(WinHost.FolderName, StringComparison.Ordinal)
            && WinHost.InstallPrefix().EndsWith("r5flowstate", StringComparison.Ordinal),
            $"{WinHost.Dir()} / {WinHost.InstallPrefix()}");

        // The one thing about the answer that is load-bearing: neither helper may be
        // loaded out of the other's folder. That mixture is the bug — the relay's
        // framework files over the host's, and a decoder that dies before it decodes
        // — and a locator answering with the wrong folder is how it comes back.
        var host = ArtDecode.FindHost();
        var relay = HostedConsole.RelayPath;

        static string ParentOf(string? path) =>
            path is null ? "" : Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);

        check("winhost: neither helper is loaded out of the other's folder",
            ParentOf(host) != relayFolder && ParentOf(relay) != hostFolder,
            host is null || relay is null
                ? "only one helper is installed here, so there is nothing to mix"
                : $"{ParentOf(host)}/ holds the host, {ParentOf(relay)}/ the relay");

        // The flat shape is legal for an install from before the split — but only
        // when one helper is flat in it. Two flat in the same folder IS the mixture,
        // whatever the folder is called, and it is a live install that needs
        // install.sh re-run rather than a code fault. Said out loud because the
        // symptom it produces (every map reported as never decoded) reads nothing
        // like its cause.
        var flatTogether = host is not null && relay is not null
            && ParentOf(host) == WinHost.FolderName && ParentOf(relay) == WinHost.FolderName
            && string.Equals(Path.GetDirectoryName(host), Path.GetDirectoryName(relay),
                StringComparison.Ordinal);

        check("winhost: the two helpers are never flat in one folder — that is the mixture",
            !flatTogether,
            flatTogether
                ? $"both are flat in {Path.GetDirectoryName(host)}/, so the relay's framework "
                  + "files are over the host's and no loadscreen will decode. Re-run install.sh."
                : host is null || relay is null
                    ? "only one helper is installed here"
                    : $"the host is in {ParentOf(host)}/, the relay in {ParentOf(relay)}/");

        // And a helper found under winhost/ has to be in a folder of its own or the
        // flat one an install from before the split has. A third shape is a folder
        // neither install.sh nor Find writes to, and the next thing to read from it
        // would be reading a stranger's files.
        static string Shape(string? path)
        {
            if (path is null)
                return "not installed on this machine";

            var parent = ParentOf(path);
            if (parent == WinHost.FolderFor(Path.GetFileName(path)))
                return $"its own folder ({parent}/)";
            if (parent == WinHost.FolderName)
                return "the flat shape (an install from before the split)";
            return path.Contains("/" + WinHost.FolderName + "/", StringComparison.Ordinal)
                ? $"NOT a folder of its own — '{parent}/'"
                : "a published build in the source tree (one project per folder)";
        }

        check("winhost: the art host is in a folder of its own, or the flat legacy one",
            host is null || !Shape(host).StartsWith("NOT", StringComparison.Ordinal),
            $"{Shape(host)} — {host ?? ArtDecode.HostExeName}");
        check("winhost: so is the relay",
            relay is null || !Shape(relay).StartsWith("NOT", StringComparison.Ordinal),
            $"{Shape(relay)} — {relay ?? HostedConsole.RelayExeName}");

        // --- and the one field that decides whether a console window appears ---
        //
        // Windows allocates a console from the PE subsystem and nothing else, and
        // Proton keeps the rule: a helper built as a console binary gets a dark Wine
        // console for every run. Both helpers were, so the player saw one window per
        // art batch and one per relay per launch. SelfTestRunner reads the projects'
        // half of the fix (the csprojs say WinExe); this is the binaries' own half,
        // and it is the half that matters — an install keeps whatever it was
        // published with until install.sh is re-run.
        void SubsystemCheck(string label, string? exe)
        {
            if (exe is null)
            {
                check($"winhost: the {label} carries no console window (PE subsystem 2)", true,
                    "skipped — not installed on this machine");
                return;
            }

            var subsystem = WinHost.SubsystemOf(exe);
            check($"winhost: the {label} carries no console window (PE subsystem 2)",
                subsystem == 2,
                subsystem is null
                    ? $"could not read a PE subsystem out of {exe}"
                    : subsystem == 2
                        ? $"subsystem 2 (GUI), from {exe}"
                        : $"subsystem {subsystem} "
                          + (subsystem == 3 ? "(console — Wine allocates a window)" : "(unexpected)")
                          + $"; {exe} predates the fix — re-run install.sh");
        }

        SubsystemCheck("art host", host);
        SubsystemCheck("relay", relay);

        // The reader itself, both ways — so a passing check above cannot be a reader
        // that answers 2 for everything. These fixtures are headers and nothing else:
        // not runnable images, which is all right, because the reader only walks to
        // the subsystem field.
        var fixtureDir = Path.Combine(Path.GetTempPath(), "r5f-pe-selftest-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(fixtureDir);
            var gui = Path.Combine(fixtureDir, "gui.exe");
            var console = Path.Combine(fixtureDir, "console.exe");
            var notPe = Path.Combine(fixtureDir, "notpe.exe");
            File.WriteAllBytes(gui, FakePe(2));
            File.WriteAllBytes(console, FakePe(3));
            File.WriteAllText(notPe, "not an image at all");

            check("winhost: the subsystem reader tells GUI from console, and refuses a non-image",
                WinHost.SubsystemOf(gui) == 2
                && WinHost.SubsystemOf(console) == 3
                && WinHost.SubsystemOf(notPe) is null
                && WinHost.SubsystemOf(null) is null
                && WinHost.IsGuiSubsystem(gui)
                && !WinHost.IsGuiSubsystem(console),
                $"gui={WinHost.SubsystemOf(gui)}, console={WinHost.SubsystemOf(console)}, "
                + $"notpe={WinHost.SubsystemOf(notPe)?.ToString() ?? "null"}");
        }
        finally
        {
            TryDelete(fixtureDir);
        }
    }

    /// <summary>
    /// A PE image with the headers a subsystem reader walks and nothing else: the
    /// DOS stub's <c>MZ</c> and <c>e_lfanew</c>, the PE signature, a PE32+ optional
    /// header, and the subsystem field 92 bytes into the PE header (24 for the
    /// signature and COFF header, 68 for the field's own place in the optional
    /// header). Long enough for the reader's 0x60-byte block, and no longer.
    /// </summary>
    static byte[] FakePe(int subsystem)
    {
        const int PeOffset = 0x40;
        const int PeBlock = 0x60;
        var image = new byte[PeOffset + PeBlock];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(PeOffset).CopyTo(image, 0x3C);
        image[PeOffset] = (byte)'P';
        image[PeOffset + 1] = (byte)'E';
        BitConverter.GetBytes((ushort)0x20B).CopyTo(image, PeOffset + 24);
        BitConverter.GetBytes((ushort)subsystem).CopyTo(image, PeOffset + 24 + 68);
        return image;
    }

    /// <summary>
    /// The Res menu's contents and the window-mode flags, against the two tools'
    /// real output as the development box produced it. Hermetic in the strong sense:
    /// every check that reads modes sets <see cref="DisplayModes.ModesOverride"/>
    /// first and clears it in a <c>finally</c>, so a suite run cannot ask this
    /// machine's session anything, and the two fixtures are the only place the
    /// parsers are pointed at a display.
    /// </summary>
    static void DisplayModesChecks(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        // kscreen-doctor -o, colour escapes and all, from a Wayland session with one
        // 1920x1080 DisplayPort panel. 18 entries, four of them 1920x1080 at different
        // refresh rates, one Geometry line and one Scale line that both hold sizes.
        const string ks = "\u001b[01;32mOutput: \u001b[0;0m1 DP-1 4a53eb49-cce8-4047-8cec-0c86e6832572\n"
            + "\t\u001b[01;32menabled\u001b[0;0m\n"
            + "\t\u001b[33mModes: \u001b[0;0m 1:\u001b[01;32m1920x1080@180.00*\u001b[0;0m!  "
            + "2:2560x1440@75.00  3:1920x1080@119.98  4:1920x1080@74.99  5:1920x1080@60.00  "
            + "6:1920x1080@59.94  7:1280x1024@75.03  8:1280x720@59.94  9:1024x768@119.99  "
            + "10:1024x768@75.03  11:1024x768@60.00  12:800x600@119.97  13:800x600@75.00  "
            + "14:800x600@60.32  15:720x480@59.94  16:640x480@119.52  17:640x480@75.00  "
            + "18:640x480@59.94 \n"
            + "\t\u001b[33mGeometry: \u001b[0;0m0,0 1920x1080\n"
            + "\t\u001b[33mScale: \u001b[0;0m1\n";

        var modes = DisplayModes.ParseKScreenDoctor(ks);
        var geometry = DisplayModes.ParseKScreenDoctorGeometry(ks);
        check("display: kscreen-doctor's mode line parses through its colour escapes",
            modes.Count == 8 && modes.Contains(new DisplayModes.DisplayMode(2560, 1440))
            && modes.Contains(new DisplayModes.DisplayMode(640, 480))
            && modes.Any(m => m.Width >= 30000) == false,
            $"{modes.Count} distinct modes: "
            + string.Join(", ", modes.Select(m => $"{m.Width}x{m.Height}")));

        check("display: four refresh rates of one size are one menu entry",
            modes.Count(m => m.Width == 1920 && m.Height == 1080) == 1,
            $"{modes.Count(m => m.Width == 1920 && m.Height == 1080)} entries for 1920x1080");

        check("display: a Geometry or Scale line is not mistaken for a mode",
            !modes.Contains(new DisplayModes.DisplayMode(0, 0))
            && !modes.Contains(new DisplayModes.DisplayMode(0, 1920))
            && modes.Count == 8,
            string.Join(", ", modes.Select(m => $"{m.Width}x{m.Height}")));

        check("display: the Geometry line is the size the session is at",
            geometry == new DisplayModes.DisplayMode(1920, 1080),
            geometry is null ? "not parsed" : $"{geometry.Value.Width}x{geometry.Value.Height}");

        // xrandr -q on the same Wayland box: it talks to Xwayland, which answers with
        // its own modelist (320x240, 320x200, 1368x768 — none of which the panel
        // offers) and a "maximum 32767 x 32767" that must never become a menu item.
        const string xr = "WARNING: running xrandr against an Xwayland server.\n"
            + "Screen 0: minimum 16 x 16, current 1920 x 1080, maximum 32767 x 32767\n"
            + "DP-1 connected primary 1920x1080+0+0 (normal left inverted right x axis y axis) 527mm x 296mm\n"
            + "   1920x1080    179.98*+\n"
            + "   1440x1080    179.92  \n"
            + "   1280x1024    179.91  \n"
            + "   1280x960     179.87  \n"
            + "   1024x768     179.84  \n"
            + "   800x600      179.71  \n"
            + "   640x480      179.43  \n"
            + "   320x240      178.06  \n"
            + "   1680x1050    179.94  \n"
            + "   1600x900     179.77  \n"
            + "   1280x720     179.72  \n";

        var xrModes = DisplayModes.ParseXrandr(xr);
        check("display: xrandr's own list parses, and its maximum is not a mode",
            xrModes.Count == 11 && xrModes.Contains(new DisplayModes.DisplayMode(320, 240))
            && xrModes.Contains(new DisplayModes.DisplayMode(1680, 1050))
            && !xrModes.Any(m => m.Width == 32767 || m.Height == 32767),
            $"{xrModes.Count} modes, none of them 32767");

        check("display: the Screen line is the size Xwayland is at",
            DisplayModes.ParseXrandrCurrent(xr) == new DisplayModes.DisplayMode(1920, 1080),
            $"{DisplayModes.ParseXrandrCurrent(xr)}");

        // The menu itself. One of everything, including two that must be dropped and
        // one that is 3:2 (720x480 — a real mode on this panel, and "Other" upstream).
        var fixture = new List<DisplayModes.DisplayMode>
        {
            new(320, 240), new(640, 480), new(1024, 768), new(1280, 1024), new(1366, 768),
            new(1680, 1050), new(1920, 1080), new(2560, 1440), new(720, 480),
        };
        DisplayModes.ModesOverride = () => fixture;
        DisplayModes.DesktopOverride = () => new DisplayModes.DisplayMode(1920, 1080);
        try
        {
            var groups = DisplayModes.Groups();
            check("display: the menu groups by aspect, in Windows' order, skipping the empty ones",
                string.Join(",", groups.Select(g => g.Caption)) == "4:3,5:4,16:10,16:9,Other",
                string.Join(", ", groups.Select(g => $"{g.Caption}({g.Modes.Count})")));

            var flat = groups.SelectMany(g => g.Modes.Select(m => $"{g.Caption}:{m.Width}x{m.Height}"));
            check("display: a mode is in the group its integer aspect says, sorted",
                string.Join(" ", flat) == "4:3:640x480 4:3:1024x768 5:4:1280x1024 "
                    + "16:10:1680x1050 16:9:1920x1080 16:9:2560x1440 Other:720x480 Other:1366x768",
                string.Join(" ", flat));

            check("display: a mode below the listed minimum is not in the menu",
                groups.SelectMany(g => g.Modes).All(m => m.Width >= 640 && m.Height >= 480)
                && !groups.SelectMany(g => g.Modes).Contains(new DisplayModes.DisplayMode(320, 240)),
                "320x240 absent");

            check("display: 1280x1024 is 5:4, not 4:3 — the integer rule, not a float one",
                DisplayModes.AspectCaption(1280, 1024) == "5:4"
                && DisplayModes.AspectCaption(1366, 768) == "Other"
                && DisplayModes.AspectCaption(1680, 1050) == "16:10"
                && DisplayModes.AspectCaption(1920, 1080) == "16:9",
                $"1280x1024={DisplayModes.AspectCaption(1280, 1024)}, "
                + $"1366x768={DisplayModes.AspectCaption(1366, 768)}");

            check("display: a fresh install launches at the session's own size when it is a listed mode",
                DisplayModes.PreferredDefaultMode() == new DisplayModes.DisplayMode(1920, 1080),
                $"{DisplayModes.PreferredDefaultMode()}");

            check("display: an unlisted session size falls back to the biggest listed mode",
                UnlistedDesktopPicks(2560, 1440),
                "3440x1440 (not listed) -> 2560x1440");

            check("display: a machine with no modes at all still has a size to launch at",
                NoModesStillHasADefault(), "no modes -> 1920x1080");
        }
        finally
        {
            DisplayModes.ModesOverride = null;
            DisplayModes.DesktopOverride = null;
            DisplayModes.Invalidate();
        }

        // The clamp, which is what stands between a typed number and the swap chain.
        check("display: the clamp range is 320–16384 and the fallback is the caller's",
            DisplayModes.ClampDimension(320, 0) == 320
            && DisplayModes.ClampDimension(16384, 0) == 16384
            && DisplayModes.ClampDimension(319, 0) == 0
            && DisplayModes.ClampDimension(16385, 0) == 0
            && DisplayModes.ClampDimension(-100, 7) == 7
            && DisplayModes.ClampDimension(0, 0) == 0,
            "319 -> fallback, 16385 -> fallback, 0 -> 0 (no override)");

        // The window mode, from every direction it can arrive.
        check("display: a window mode is one of the three, whatever the settings file said",
            DisplayModes.ClampWindowMode("windowed") == "windowed"
            && DisplayModes.ClampWindowMode("  Borderless ") == "borderless"
            && DisplayModes.ClampWindowMode("FULLSCREEN") == "fullscreen"
            && DisplayModes.ClampWindowMode("") == "fullscreen"
            && DisplayModes.ClampWindowMode(null) == "fullscreen"
            && DisplayModes.ClampWindowMode("maximized") == "fullscreen"
            && DisplayModes.WindowModes.Count == 3,
            "an empty setting is fullscreen, like a fresh Windows install");

        // The three flag shapes, which is the whole point of the setting: they are
        // upstream's, and the only difference between borderless and windowed is
        // -noborder beside it.
        var windowedLine = ClientLine("windowed", 1920, 1080);
        var borderlessLine = ClientLine("borderless", 1920, 1080);
        var fullscreenLine = ClientLine("fullscreen", 1920, 1080);
        check("display: windowed asks for a window, and does not also ask for a border",
            windowedLine.Contains("-windowed") && !windowedLine.Contains("-noborder")
            && !windowedLine.Contains("-fullscreen"),
            "[" + string.Join(' ', windowedLine.Where(a => a.StartsWith('-'))) + "]");
        check("display: borderless is windowed plus -noborder",
            borderlessLine.Contains("-windowed") && borderlessLine.Contains("-noborder")
            && !borderlessLine.Contains("-fullscreen"),
            "[" + string.Join(' ', borderlessLine.Where(a => a.StartsWith('-'))) + "]");
        check("display: fullscreen asks for fullscreen and no window flag",
            fullscreenLine.Contains("-fullscreen") && !fullscreenLine.Contains("-windowed")
            && !fullscreenLine.Contains("-noborder"),
            "[" + string.Join(' ', fullscreenLine.Where(a => a.StartsWith('-'))) + "]");
        check("display: the size rides with the mode, once each",
            windowedLine.Count(a => a == "-width") == 1 && windowedLine.Count(a => a == "-height") == 1
            && Pair(windowedLine, "-width") == "1920" && Pair(windowedLine, "-height") == "1080",
            Pair(windowedLine, "-width") + "x" + Pair(windowedLine, "-height"));

        var defaultLine = ClientLine("fullscreen", 0, 0);
        check("display: 0 x 0 (the menu's Game default) puts no size on the line at all",
            !defaultLine.Contains("-width") && !defaultLine.Contains("-height")
            && defaultLine.Contains("-fullscreen"),
            "[" + string.Join(' ', defaultLine.Where(a => a.StartsWith('-'))) + "]");

        // The menu's three captions have to be real strings. CheckLocKeys cannot see
        // them: the key is built from the mode name at run time, so no literal
        // `Loc.Get("window_mode_...")` exists anywhere for the source scan to find.
        var missing = DisplayModes.WindowModes
            .Where(m => Loc.Get(DisplayModes.LocKey(m)) == DisplayModes.LocKey(m))
            .ToList();
        check("display: the three window modes have their menu captions",
            missing.Count == 0,
            missing.Count == 0 ? string.Join(" / ",
                DisplayModes.WindowModes.Select(m => Loc.Get(DisplayModes.LocKey(m))))
                : "missing: " + string.Join(", ", missing));

        check("display: the fallback list is five ordinary sizes, all inside the clamp",
            DisplayModes.Presets().Count == 5
            && DisplayModes.Presets().Contains(new DisplayModes.DisplayMode(1920, 1080))
            && DisplayModes.Presets().All(m =>
                DisplayModes.ClampDimension(m.Width, 0) == m.Width
                && DisplayModes.ClampDimension(m.Height, 0) == m.Height),
            string.Join(", ", DisplayModes.Presets().Select(m => $"{m.Width}x{m.Height}")));
    }

    // ----------------------------------------------------------- hosted console

    /// <summary>
    /// The hosted console's launcher end: the environment the game is handed, the
    /// relay's command line, the handshake, the connect-until-the-deadline rule, and
    /// the socket-backed tap itself.
    ///
    /// <para>All of it runs without a prefix, a Proton build or a game, because the
    /// channel is testable without them: the relay's far side is a socket, so a
    /// listener in this process stands in for it exactly, and the tap's one question
    /// about its child — "are you still there?" — is answered by this process's own
    /// <see cref="System.Diagnostics.Process"/> handle. Nothing is spawned, and the
    /// only thing read from the machine is whether a relay happens to be installed.</para>
    /// </summary>
    static void HostedConsoleChecks(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        Directory.CreateDirectory(root);

        const string fixtureOut = @"\\.\pipe\r5f-con-s-fixture";
        const string fixtureIn = @"\\.\pipe\r5f-in-s-fixture";
        var fixtureHello = HostedConsole.HelloTag + "\ts\t" + fixtureOut + "\t" + fixtureIn;

        // ---- the game's environment
        var hosted = HostedConsole.EnvironmentFor("s", fixtureOut, fixtureIn);
        check("console: the game is told to host its console, and on which two pipes",
            hosted.Count == 4
            && hosted[HostedConsole.HostedEnv] == "1"
            && hosted[HostedConsole.PipeEnv] == fixtureOut
            && hosted[HostedConsole.InEnv] == fixtureIn
            && hosted[HostedConsole.RoleEnv] == "s",
            string.Join(" ", hosted.Select(kv => kv.Key + "=" + kv.Value)));

        var cleared = HostedConsole.ClearedEnvironment();
        check("console: a launch with no relay is told so, in the same four names",
            cleared.Count == 4
            && cleared[HostedConsole.HostedEnv] == "0"
            && cleared[HostedConsole.PipeEnv].Length == 0
            && cleared[HostedConsole.InEnv].Length == 0
            && cleared[HostedConsole.RoleEnv].Length == 0,
            string.Join(" ", cleared.Select(kv => kv.Key + "='" + kv.Value + "'")));

        var merged = HostedConsole.MergeEnvironment(
            new Dictionary<string, string>
            {
                ["R5F_LOCAL_RCON"] = "1",
                [HostedConsole.HostedEnv] = "stale",
            },
            hosted);
        check("console: the hosted block wins over anything a caller passes, and keeps the rest",
            merged["R5F_LOCAL_RCON"] == "1" && merged[HostedConsole.HostedEnv] == "1"
            && merged[HostedConsole.RoleEnv] == "s" && merged.Count == 5,
            string.Join(" ", merged.Select(kv => kv.Key + "=" + kv.Value)));

        check("console: the role tag is the one the pipe names carry, for both roles",
            HostedConsole.Tag(LaunchRole.Dedicated) == "s"
            && HostedConsole.Tag(LaunchRole.Client) == "c"
            && HostedConsole.EnvironmentFor("c", fixtureOut, fixtureIn)[HostedConsole.RoleEnv] == "c");

        // ---- the relay's command line, and the port it is given
        var relayArgs = HostedConsole.RelayArgs(LaunchRole.Dedicated, 37015);
        check("console: the relay is told the port to listen on and the role, and nothing else",
            relayArgs.Count == 4 && relayArgs[0] == "--port" && relayArgs[1] == "37015"
            && relayArgs[2] == "--role" && relayArgs[3] == "s"
            && HostedConsole.RelayArgs(LaunchRole.Client, 1)[3] == "c",
            string.Join(' ', relayArgs));

        var freePort = HostedConsole.FreePort();
        check("console: the port for the relay is a real free one",
            freePort is > 1024 and < 65536 && HostedConsole.FreePort() is > 0,
            $"127.0.0.1:{freePort}");

        // ---- the handshake: the one thing between a half-parsed pipe path and a
        // game with no console at all
        check("console: the relay's first line names both pipes and the role it is",
            HostedConsole.TryParseHello(fixtureHello, out var tag, out var outPipe, out var inPipe)
            && tag == "s" && outPipe == fixtureOut && inPipe == fixtureIn,
            fixtureHello);

        var notAHandshake = new (string What, string? Line)[]
        {
            ("no line at all", null),
            ("an empty line", ""),
            ("a line that says something else", "ready\tgo"),
            ("the handshake without the role", HostedConsole.HelloTag + "\t" + fixtureOut + "\t" + fixtureIn),
            ("a role that is neither s nor c", HostedConsole.HelloTag + "\tx\t" + fixtureOut + "\t" + fixtureIn),
            ("a handshake with no pipe paths", HostedConsole.HelloTag + "\ts\t\t"),
            ("one pipe, named twice", HostedConsole.HelloTag + "\ts\t" + fixtureOut + "\t" + fixtureOut),
            ("the word with a suffix", "hello2\ts\t" + fixtureOut + "\t" + fixtureIn),
            ("a handshake with something after it", fixtureHello + "\textra"),
        };
        string? accepted = null;
        foreach (var candidate in notAHandshake)
        {
            if (HostedConsole.TryParseHello(candidate.Line, out _, out _, out _))
                accepted = candidate.What;
        }
        check($"console: {notAHandshake.Length} things that are not a handshake are all refused",
            accepted is null, accepted is null ? "all refused" : "accepted: " + accepted);

        // ---- readiness is the connect, because the relay's ready line goes to a
        // stream Proton discards
        var listening = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listening.Start();
        var listeningOn = ((System.Net.IPEndPoint)listening.LocalEndpoint).Port;
        try
        {
            var accept = listening.AcceptTcpClientAsync();
            var client = HostedConsole.ConnectAsync(listeningOn, TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();
            var served = accept.Wait(TimeSpan.FromSeconds(2)) ? accept.Result : null;
            check("console: a relay that is listening is connected to, and the accept is the signal",
                client is not null && served is not null && served.Connected,
                client is null ? "no connect" : $"connected on 127.0.0.1:{listeningOn}");
            client?.Dispose();
            served?.Dispose();
        }
        finally
        {
            listening.Stop();
        }

        var deadPort = FreePort();
        var retried = System.Diagnostics.Stopwatch.StartNew();
        var nothing = HostedConsole.ConnectAsync(deadPort, TimeSpan.FromMilliseconds(400))
            .GetAwaiter().GetResult();
        retried.Stop();
        check("console: a relay that never answers is retried until the deadline, then given up on",
            nothing is null && retried.ElapsedMilliseconds >= 300 && retried.ElapsedMilliseconds < 5000,
            $"127.0.0.1:{deadPort} -> {(nothing is null ? "nothing" : "connected")} "
            + $"after {retried.ElapsedMilliseconds} ms");

        check("console: a port the picker never produced is not even tried",
            HostedConsole.ConnectAsync(0, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult() is null);

        // ---- the socket tap, against a relay this process stands in for
        var relay = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        relay.Start();
        var relayPort = ((System.Net.IPEndPoint)relay.LocalEndpoint).Port;
        var lines = new List<string>();
        var commands = new List<string>();
        var relaySawEnd = new System.Threading.ManualResetEventSlim(false);

        var standin = new System.Threading.Thread(() =>
        {
            try
            {
                using var served = relay.AcceptTcpClient();
                using var stream = served.GetStream();
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var talk = new StreamWriter(stream, utf8, 256, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };
                using var hear = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096, leaveOpen: true);

                // The handshake first, then three console lines: an ordinary one, a
                // blank one (the engine prints them and the pane keeps them), and one
                // carrying colour escapes, which must survive the framing untouched.
                talk.Write(fixtureHello);
                talk.Write('\n');
                talk.Write("hostname: r5f-dedi");
                talk.Write('\n');
                talk.Write("");
                talk.Write('\n');
                talk.Write("\u001b[38;2;255;0;0mred\u001b[0m");
                talk.Write('\n');
                talk.Flush();

                // EOF here is the launcher detaching the console: the only end this
                // channel has.
                while (hear.ReadLine() is { } command)
                {
                    lock (commands)
                        commands.Add(command);
                }
            }
            catch
            {
                // The socket went away under the read, which is how this thread ends
                // when the check above failed somewhere else.
            }
            finally
            {
                relaySawEnd.Set();
            }
        })
        { IsBackground = true, Name = "r5f-standin-relay" };
        standin.Start();

        try
        {
            var socket = HostedConsole.ConnectAsync(relayPort, TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();
            var channel = socket!.GetStream();
            var channelUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var channelReader = new StreamReader(channel, channelUtf8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

            // Read exactly as HostedConsole.StartAsync does: one handshake line, and
            // everything after it belongs to the tap.
            var helloLine = channelReader.ReadLine();
            var handshakeOk = HostedConsole.TryParseHello(helloLine, out var wireTag,
                out var wireOut, out var wireIn);

            var writer = new StreamWriter(channel, channelUtf8, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            bool Send(string one)
            {
                try
                {
                    lock (writer)
                    {
                        writer.Write(one);
                        writer.Write('\n');
                        writer.Flush();
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            // The child is this process, because the tap's only question about it is
            // whether it is still there. The socket is the owner: that is the part of
            // HostedConsole's teardown that ends the relay's read loop.
            var child = System.Diagnostics.Process.GetCurrentProcess();
            var tap = ConsoleTap.OverSocket(LaunchRole.Dedicated, child, channelReader, Send,
                owned: socket);
            tap.LineReceived += line =>
            {
                lock (lines)
                    lines.Add(line);
            };
            tap.Start();

            check("console: the handshake is read off the wire before the tap exists",
                handshakeOk && wireTag == "s" && wireOut == fixtureOut && wireIn == fixtureIn,
                helloLine ?? "(nothing at all)");

            WaitFor(lines, 3, 3000);
            var got = Snapshot(lines);
            check("console: the game's lines arrive over the socket, blank ones included",
                got.Count == 3 && got[0] == "hostname: r5f-dedi" && got[1].Length == 0
                && got[2] == "\u001b[38;2;255;0;0mred\u001b[0m",
                got.Count == 0 ? "(nothing arrived)" : string.Join(" | ", got.Select(Show)));

            check("console: the socket tap's child is the game, not the relay",
                tap.Role == LaunchRole.Dedicated && ReferenceEquals(tap.Child, child));

            check("console: a command goes out as one line, and the relay reads it back whole",
                tap.TryWriteCommand("bridge_setmode mp_rr_desertlands_hu")
                && WaitFor(commands, 1, 2000)
                && Snapshot(commands)[0] == "bridge_setmode mp_rr_desertlands_hu",
                Snapshot(commands).Count == 0 ? "(nothing arrived)" : Show(Snapshot(commands)[0]));

            // An embedded newline would queue a second command nobody authorised, so
            // it ends the line instead — upstream's rule at the pipe.
            check("console: a command with a newline in it is cut, not queued as two",
                tap.TryWriteCommand("say hello\nbridge_setmode mp_rr_x")
                && WaitFor(commands, 2, 2000) && Snapshot(commands)[1] == "say hello",
                Snapshot(commands).Count < 2 ? "(nothing arrived)" : Show(Snapshot(commands)[1]));

            var before = Snapshot(commands).Count;
            var refused = new[]
            {
                new string('a', 600),
                "   ",
                "",
            }.Count(c => !tap.TryWriteCommand(c));
            System.Threading.Thread.Sleep(200);
            check("console: a command the tap refuses never reaches the game",
                refused == 3 && Snapshot(commands).Count == before,
                $"{refused} of 3 refused, {Snapshot(commands).Count - before} sent anyway");

            tap.Dispose();
            check("console: detaching the console ends the channel, and the relay sees it",
                relaySawEnd.Wait(3000));
        }
        finally
        {
            relay.Stop();
        }

        // ---- no relay, and no Proton to run one with: one line each, and no launch
        // lost
        var said = new List<string>();
        var realRelay = HostedConsole.RelayPathOverride;
        try
        {
            // A path that is not there, rather than a null answer: the seam's null
            // means "ask WinHost", so this is how a machine without the relay — or
            // with a half-copied one — is stood in for. The line has to name the file
            // and the folder it belongs in, because it is what the player is told to
            // go and look at.
            HostedConsole.RelayPathOverride = () => Path.Combine(root, "not-installed", "r5f-relay.exe");
            var noRelay = HostedConsole
                .StartAsync(Path.Combine(root, "no-proton"), Path.Combine(root, "no-prefix"), "",
                    LaunchRole.Client, said.Add)
                .GetAwaiter().GetResult();
            check("console: a machine with no relay installed costs one log line, not a launch",
                noRelay is null && said.Count == 1
                && said[0].Contains("console relay is not installed", StringComparison.Ordinal)
                && said[0].Contains(HostedConsole.RelayExeName, StringComparison.Ordinal)
                && said[0].Contains("winhost", StringComparison.Ordinal)
                && said[0].Contains("install.sh", StringComparison.Ordinal),
                said.Count == 0 ? "(nothing was said)" : said[0]);

            said.Clear();
            HostedConsole.RelayPathOverride = () => Write(root, "r5f-relay.exe", new[] { "not really an exe" });
            var noProton = HostedConsole
                .StartAsync(Path.Combine(root, "no-proton"), Path.Combine(root, "no-prefix"), "",
                    LaunchRole.Client, said.Add)
                .GetAwaiter().GetResult();
            check("console: a relay with no Proton build to run it through is the same answer",
                noProton is null && said.Count == 1
                && said[0].Contains("No Proton build", StringComparison.Ordinal),
                said.Count == 0 ? "(nothing was said)" : said[0]);
        }
        finally
        {
            HostedConsole.RelayPathOverride = realRelay;
        }

        check("console: the relay is looked for by name, and not having one is not an error",
            HostedConsole.RelayExeName == "r5f-relay.exe"
            && (HostedConsole.RelayPath is null
                || Path.GetFileName(HostedConsole.RelayPath) == HostedConsole.RelayExeName),
            HostedConsole.RelayPath ?? "not installed on this machine");
    }

    /// <summary>Poll a fixture collection a stand-in relay's thread appends to.</summary>
    static bool WaitFor(List<string> lines, int count, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (Snapshot(lines).Count >= count)
                return true;
            System.Threading.Thread.Sleep(20);
        }
        return Snapshot(lines).Count >= count;
    }

    static List<string> Snapshot(List<string> lines)
    {
        lock (lines)
            return new List<string>(lines);
    }

    /// <summary>One line for a detail message, with its escape visible: a blank line
    /// and a line that is only colour must not look the same in a failure report.</summary>
    static string Show(string raw) => "\"" + raw.Replace("\u001b", "\\e") + "\"";

    /// <summary>With the seams cleared, so a nested check cannot leave them set.</summary>
    static bool UnlistedDesktopPicks(int width, int height)
    {
        DisplayModes.DesktopOverride = () => new DisplayModes.DisplayMode(3440, 1440);
        try
        {
            return DisplayModes.PreferredDefaultMode() == new DisplayModes.DisplayMode(width, height);
        }
        finally
        {
            DisplayModes.DesktopOverride = null;
        }
    }

    static bool NoModesStillHasADefault()
    {
        DisplayModes.ModesOverride = () => Array.Empty<DisplayModes.DisplayMode>();
        try
        {
            return DisplayModes.PreferredDefaultMode() == new DisplayModes.DisplayMode(1920, 1080);
        }
        finally
        {
            DisplayModes.ModesOverride = null;
        }
    }

    static List<string> ClientLine(string windowMode, int width, int height)
        => LaunchArgBuilder.BuildClient(
            dev: false, offlineNoAuth: false, forceOnline: false,
            language: "english", extra: null, windowMode: windowMode,
            width: width, height: height, map: null, password: null, connect: null);

    static string Pair(IReadOnlyList<string> line, string flag)
    {
        for (var i = 0; i + 1 < line.Count; i++)
        {
            if (line[i] == flag)
                return line[i + 1];
        }
        return "";
    }

    static string Write(string dir, string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
