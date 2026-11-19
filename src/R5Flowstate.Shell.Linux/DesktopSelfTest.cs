using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// The launcher's own desktop-facing pieces, the ones that have nothing to do
/// with the game: the install gate that decides whether a root holds a game (and
/// which a platform-only root used to fool), the log file that outlives the
/// window, the reading of an EA install attempt, and the menu entries.
///
/// Everything here runs in a temp folder with its roots redirected — the real
/// <c>~/.local/share/applications</c>, the real icon theme and the real log are
/// never opened. The two exceptions are deliberate and read-only: the newest-EA-log
/// scans a synthetic prefix, and the shortcut writer is asked what it does when
/// the icon is missing.
/// </summary>
static class DesktopSelfTest
{
    public static void Run(Action<string, bool, string> report)
    {
        // Defaulted detail, so the groups below read as one line per property.
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var root = Path.Combine(Path.GetTempPath(), "r5f-desktop-selftest-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(root);
            Presence(check, Path.Combine(root, "presence"));
            LogFile(check, Path.Combine(root, "log"));
            EaWatch(check);
            EaPhases(check);
            EaLogs(check, Path.Combine(root, "prefix"));
            Shortcuts(check, Path.Combine(root, "shortcuts"));
        }
        catch (Exception ex)
        {
            check("desktop pieces", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------- install presence

    /// <summary>
    /// The gate every install-shaped decision goes through. The case that matters
    /// is the third and fourth: on Linux the platform lane ships a client.dll of
    /// its own, so the Windows marker list says "game" for a root that has no
    /// game — and the install's own record has to be allowed to say otherwise.
    /// </summary>
    static void Presence(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        var exe = Path.Combine(root, "exe");
        var platformOnly = Path.Combine(root, "platform-only");
        var platformNoRecord = Path.Combine(root, "platform-no-record");
        var platformReady = Path.Combine(root, "platform-ready");

        // 1. The game's binary: no record needed, same as Windows.
        Touch(Path.Combine(exe, "r5apex.exe"));
        check("presence: r5apex.exe is a game", InstallPresence.GamePresent(exe));

        // 2. The dedicated-server binary counts too, as it does upstream.
        var dedi = Path.Combine(root, "dedi");
        Touch(Path.Combine(dedi, "r5apex_ds.exe"));
        check("presence: r5apex_ds.exe is a game", InstallPresence.GamePresent(dedi));

        // 3. The bug this file exists for: a platform-only root. The lane's own
        //    client.dll is there and the install record says the client half is
        //    not — so it is not a game, whatever the marker list thinks.
        Touch(Path.Combine(platformOnly, "client.dll"));
        SaveState(platformOnly, clientReady: false);
        check("presence: platform-only root is not a game",
            !InstallPresence.GamePresent(platformOnly));

        // 4. client.dll with no record at all: a hand-copied game, where
        //    Windows' rule stands.
        Touch(Path.Combine(platformNoRecord, "client.dll"));
        check("presence: client.dll with no record is a game",
            InstallPresence.GamePresent(platformNoRecord));

        // 5. client.dll and a record that says the client lane is done.
        Touch(Path.Combine(platformReady, "client.dll"));
        SaveState(platformReady, clientReady: true);
        check("presence: client.dll with a ready record is a game",
            InstallPresence.GamePresent(platformReady));

        // 6. A game binary wins over a record that says otherwise: the record is
        //    bookkeeping, the binary is the thing that runs.
        var both = Path.Combine(root, "both");
        Touch(Path.Combine(both, "r5apex.exe"));
        Touch(Path.Combine(both, "client.dll"));
        SaveState(both, clientReady: false);
        check("presence: the binary outranks the record", InstallPresence.GamePresent(both));

        // 7. Nothing, and nowhere.
        check("presence: an empty root is not a game", !InstallPresence.GamePresent(root));
        check("presence: no path is not a game", !InstallPresence.GamePresent(null));
        check("presence: a missing root is not a game",
            !InstallPresence.GamePresent(Path.Combine(root, "nowhere")));

        // 8. A record that cannot be parsed must not hide a working install.
        var broken = Path.Combine(root, "broken-record");
        Touch(Path.Combine(broken, "client.dll"));
        File.WriteAllText(Path.Combine(broken, ProductConstants.InstallStateFileName), "{ not json");
        check("presence: an unreadable record keeps the game visible",
            InstallPresence.GamePresent(broken));
    }

    static void SaveState(string root, bool clientReady)
        => InstallStateIO.Save(Path.Combine(root, ProductConstants.InstallStateFileName),
            new InstallState { ClientReady = clientReady, PlatformReady = !clientReady });

    // ----------------------------------------------------------- launcher log

    /// <summary>
    /// The log the window's pane is not: it survives the process, so a launch
    /// that failed after the window closed still has an account. Rotating and
    /// timestamps are what make it worth keeping; never throwing is what keeps it
    /// from becoming a new failure of its own.
    /// </summary>
    static void LogFile(Action<string, bool, string> report, string dir)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        var previous = LauncherLog.LogPath;
        try
        {
            var path = Path.Combine(dir, "launcher.log");
            LauncherLog.LogPath = path;

            LauncherLog.Append("first line");
            LauncherLog.Append("second line");
            check("log: appends create the file and the directory",
                File.Exists(path) && LauncherLog.Tail(10).Count == 2, string.Join(" | ", LauncherLog.Tail(10)));

            var body = File.ReadAllText(path);
            check("log: lines are timestamped",
                body.Contains("] first line") && body.StartsWith("[") && body.Contains("] "),
                body.Split('\n')[0]);
            check("log: Tail counts from the end",
                LauncherLog.Tail(1).Single().EndsWith("second line", StringComparison.Ordinal));
            check("log: an empty line is not written",
                !LauncherLog.Tail(10).Any(l => l.TrimEnd().EndsWith(']')));
            LauncherLog.Append("   ");
            check("log: whitespace is not written either", LauncherLog.Tail(10).Count == 2);

            // Rotation: the next append after the cap moves the file aside.
            LauncherLog.MaxBytes = 0;
            LauncherLog.Append("after the cap");
            check("log: rotates at the cap",
                File.Exists(LauncherLog.RotatedPath)
                && LauncherLog.Tail(1).Single().EndsWith("after the cap", StringComparison.Ordinal),
                Path.GetFileName(LauncherLog.RotatedPath));
            check("log: the previous file is kept",
                File.ReadAllLines(LauncherLog.RotatedPath).Any(l => l.EndsWith("second line", StringComparison.Ordinal)));
            LauncherLog.MaxBytes = 2 * 1024 * 1024;

            // A path that cannot be written is not a reason to fail anything.
            LauncherLog.LogPath = Path.Combine(dir, "no-such-dir", "\0bad", "x.log");
            LauncherLog.Append("this must not throw");
            check("log: an unwritable path is silent, not fatal", true, "no exception");
            LauncherLog.LogPath = path;
        }
        catch (Exception ex)
        {
            check("log file", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            LauncherLog.LogPath = previous;
        }
    }

    // ------------------------------------------------------------ EA watching

    /// <summary>
    /// How an EA install attempt reads from the outside. These are the rules that
    /// turn "I pressed Install and nothing happened" into a sentence: the desktop
    /// appearing is success, an exit with a log behind it is a stall, an exit with
    /// nothing written is a bundle that died before it spoke, and a live installer
    /// past the ceiling is simply slow.
    ///
    /// The load-bearing fixture is "a quiet log beside a live installer is not a
    /// death" — 2026-09-22, when the rules had no liveness input and a bundle
    /// downloading 234 MB was reported as having exited. Every other check here is
    /// the shape of the same lesson: silence is a fact about a log, not a process.
    ///
    /// The phase and failure fixtures quote the real attempt's own log lines, so
    /// the reader is tested against the thing it has to read.
    /// </summary>
    static void EaWatch(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        var poll = EaInstallWatch.PollEvery;
        var quiet = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(32);  // the real one
        var fresh = TimeSpan.FromSeconds(2);
        var old = EaInstallWatch.StopAfter + TimeSpan.FromSeconds(1);

        check("ea watch: a desktop is success",
            EaInstallWatch.Classify(true, true, old, installerAlive: false) == EaInstallOutcome.DesktopInstalled);

        // The bug that wrote a false report, as a fixture.
        check("ea watch: a quiet log beside a live installer is not a death",
            EaInstallWatch.Classify(false, true, quiet, installerAlive: true) == EaInstallOutcome.Running,
            EaInstallWatch.Classify(false, true, quiet, installerAlive: true).ToString());
        check("ea watch: no log beside a live installer is not a death either",
            EaInstallWatch.Classify(false, false, quiet, installerAlive: true) == EaInstallOutcome.Running);
        check("ea watch: the same attempt, once it has really gone, is a stall",
            EaInstallWatch.Classify(false, true, TimeSpan.FromSeconds(40), installerAlive: false) == EaInstallOutcome.LogStalled);
        check("ea watch: gone with nothing written means it never spoke",
            EaInstallWatch.Classify(false, false, TimeSpan.FromSeconds(40), installerAlive: false) == EaInstallOutcome.NoTrace);
        check("ea watch: alive past the ceiling is slow, not dead",
            EaInstallWatch.Classify(false, true, old, installerAlive: true) == EaInstallOutcome.StillWorking);
        check("ea watch: the ceiling outlasts a 234 MB download",
            EaInstallWatch.StopAfter > TimeSpan.FromMinutes(10),
            $"{EaInstallWatch.StopAfter.TotalMinutes} min");

        check("ea watch: only Running is not final",
            !EaInstallWatch.IsFinal(EaInstallOutcome.Running)
            && EaInstallWatch.IsFinal(EaInstallOutcome.LogStalled)
            && EaInstallWatch.IsFinal(EaInstallOutcome.NoTrace)
            && EaInstallWatch.IsFinal(EaInstallOutcome.StillWorking)
            && EaInstallWatch.IsFinal(EaInstallOutcome.DesktopInstalled));

        check("ea watch: a missing file has no size",
            EaInstallWatch.BytesOf(null) == 0 && EaInstallWatch.BytesOf("/no/such/file") == 0);
        var sized = Path.Combine(Path.GetTempPath(), "r5f-ea-size-" + Environment.ProcessId);
        File.WriteAllText(sized, "0123456789");
        check("ea watch: size is the file's", EaInstallWatch.BytesOf(sized) == 10);

        // TailText: a read that starts mid-file starts mid-line, and half a line
        // is not a line.
        File.WriteAllText(sized, "first line\nsecond line\nthird line\n");
        var tail = EaInstallWatch.TailText(sized, 12);
        check("ea watch: the tail of a log starts on a line boundary",
            tail.StartsWith("third line", StringComparison.Ordinal), tail.Replace("\n", "\\n"));
        check("ea watch: a log that is not there reads as nothing",
            EaInstallWatch.TailText("/no/such/file") == "" && EaInstallWatch.TailText(null) == "");
        File.Delete(sized);

        check("ea watch: the poll is well under the ceiling", poll < EaInstallWatch.StopAfter,
            $"poll {poll.TotalSeconds}s, ceiling {EaInstallWatch.StopAfter.TotalSeconds}s");
    }

    /// <summary>The phases of the real 2026-09-22 attempt, as its own log wrote
    /// them — because reading the phase is what turns five silent minutes into
    /// "it is downloading 234 MB" instead of a hang.</summary>
    static void EaPhases(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        const string register = "[019C:01A0][2026-09-22T04:16:01]i320: Registering bundle dependency provider: {ce0db490-17d4-4bc4-8b81-5f4242901d07}, version: 13.791.0.6304";
        const string cache = "[0138:01B4][2026-09-22T04:16:01]i000: EAXBA  TRACE (BootstrapperApplication::OnCachePackageBegin:1774) wzPackageId=EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi, cCachePayloads=1, dw64PackageCacheSize=245878784";
        const string acquire = "[0138:01B4][2026-09-22T04:16:01]i338: Acquiring package: EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi, payload: EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi, download from: https://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAapp-13.791.0.6304-15605516.msi";
        const string verify = "[019C:01B0][2026-09-22T04:21:39]i305: Verified acquired payload: EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi at path: C:\\ProgramData\\Package Cache\\.unverified\\EAapp-13.791.0.6304-15605516.msi, moving to: C:\\ProgramData\\Package Cache\\{C2622085-ABD2-49E5-8AB9-D3D6A642C091}v12.0.0.0\\EAapp-13.791.0.6304-15605516.msi.";
        const string apply = "[019C:01A0][2026-09-22T04:21:39]i301: Applying execute package: EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi, action: Install, path: C:\\ProgramData\\Package Cache\\{C2622085-ABD2-49E5-8AB9-D3D6A642C091}v12.0.0.0\\EAapp-13.791.0.6304-15605516.msi";
        const string failed = "[019C:01A0][2026-09-22T04:21:40]e000: Error 0x80070643: Failed to install MSI package.";
        const string rollback = "[0138:013C][2026-09-22T04:21:40]i319: Applied rollback package: EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi, result: 0x0, restart: None";

        check("ea phases: registering reads as registering",
            EaInstallWatch.PhaseOf(register) == EaInstallPhase.Registering);
        check("ea phases: acquiring reads as downloading",
            EaInstallWatch.PhaseOf(acquire) == EaInstallPhase.Downloading);
        check("ea phases: a verified payload reads as verifying",
            EaInstallWatch.PhaseOf(verify) == EaInstallPhase.Verifying);
        check("ea phases: applying reads as installing",
            EaInstallWatch.PhaseOf(apply) == EaInstallPhase.Installing);
        check("ea phases: the failure and its rollback read as failed",
            EaInstallWatch.PhaseOf(failed + "\n" + rollback) == EaInstallPhase.Failed);

        // The last marker wins: the log is a history, and where it ends is where
        // the attempt actually is.
        check("ea phases: the newest marker wins, not the first",
            EaInstallWatch.PhaseOf(register + "\n" + acquire + "\n" + verify + "\n" + apply)
                == EaInstallPhase.Installing);
        check("ea phases: an empty or unknown log has no phase",
            EaInstallWatch.PhaseOf("") == EaInstallPhase.Unknown
            && EaInstallWatch.PhaseOf(null) == EaInstallPhase.Unknown
            && EaInstallWatch.PhaseOf("Loading map mp_lobby") == EaInstallPhase.Unknown);

        check("ea phases: the payload size is Burn's own number",
            EaInstallWatch.PackageBytes(cache) == 245878784,
            EaInstallWatch.PackageBytes(cache).ToString());
        check("ea phases: no size without the variable",
            EaInstallWatch.PackageBytes(apply) == 0 && EaInstallWatch.PackageBytes(null) == 0);

        var downloadLine = EaInstallWatch.PhaseLine(EaInstallPhase.Downloading,
            EaInstallWatch.PackageBytes(cache));
        check("ea phases: the download line says how big and that silence is normal",
            downloadLine is not null && downloadLine.Contains("234 MB", StringComparison.Ordinal)
            && downloadLine.Contains("not a stall", StringComparison.Ordinal), downloadLine ?? "null");
        check("ea phases: every phase but Unknown has a sentence",
            EaInstallWatch.PhaseLine(EaInstallPhase.Unknown, 0) is null
            && EaInstallWatch.PhaseLine(EaInstallPhase.Registering, 0) is not null
            && EaInstallWatch.PhaseLine(EaInstallPhase.Downloading, 0) is not null
            && EaInstallWatch.PhaseLine(EaInstallPhase.Verifying, 0) is not null
            && EaInstallWatch.PhaseLine(EaInstallPhase.Installing, 0) is not null
            && EaInstallWatch.PhaseLine(EaInstallPhase.Failed, 0) is not null);

        var ratt = "[0138:013C][2026-09-22T04:21:40]i000: EAXBA  INFO  (BootstrapperApplication::logRattError:3331) ecod[0x80070643 aka 'INST-14-1603'], estr[Installation failure.], einv[true], emsg[unknown]";
        var quietLines = EaInstallWatch.FailureLines(
            register + "\n" + apply + "\n" + failed + "\n" + ratt + "\n" + rollback);
        check("ea failures: only the lines that say how it ended are quoted",
            quietLines.Count == 3 && quietLines.Any(l => l.Contains("0x80070643", StringComparison.Ordinal))
            && quietLines.Any(l => l.Contains("logRattError", StringComparison.Ordinal))
            && !quietLines.Any(l => l.Contains("Registering bundle", StringComparison.Ordinal)),
            string.Join(" | ", quietLines));
        check("ea failures: nothing to quote from an empty log",
            EaInstallWatch.FailureLines("").Count == 0 && EaInstallWatch.FailureLines(null).Count == 0);

        // The MSI's log path: named by the bundle, and not the rollback log, which
        // is named in the same family and comes later in the file.
        var variables = "[0138:013C][2026-09-22T04:22:14]i410: Variable: WixBundleLog = C:\\users\\vibrantvibez\\AppData\\Local\\Temp\\EA_app_20260922041557.log\n"
            + "[0138:013C][2026-09-22T04:22:14]i410: Variable: WixBundleLog_EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi = C:\\users\\vibrantvibez\\AppData\\Local\\Temp\\EA_app_20260922041557_000_EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi.log\n"
            + "[0138:013C][2026-09-22T04:22:14]i410: Variable: WixBundleRollbackLog_EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi = C:\\users\\vibrantvibez\\AppData\\Local\\Temp\\EA_app_20260922041557_000_EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi_rollback.log";
        check("ea failures: the MSI's own log is found, not the rollback log",
            EaInstallWatch.MsiLogPath(variables)?.EndsWith("_15605516.msi.log", StringComparison.Ordinal) == true,
            EaInstallWatch.MsiLogPath(variables) ?? "null");
        check("ea failures: no MSI log named, none claimed",
            EaInstallWatch.MsiLogPath(apply) is null && EaInstallWatch.MsiLogPath(null) is null);

        // Wine's own output, quoted from the 04:35 attempt: this is what the first
        // attempt threw away and what refuted the old verdict.
        var wineStderr =
            "01b0:fixme:advapi:DecryptFileW (L\"C:\\ProgramData\\Package Cache\\{C2622085-ABD2-49E5-8AB9-D3D6A642C091}v12.0.0.0\\EAapp-13.791.0.6304-15605516.msi\", 00000000): stub\n"
            + "01a0:err:msi:MsiEnableLogW unable to enable log L\"C:\\users\\vibrantvibez\\AppData\\Local\\Temp\\EA_app_20260922043506_000_EAapp_13.791.0.6304_976251a73_b197ac3e_15605516.msi.log\" (32)\n"
            + "01dc:err:environ:init_peb starting L\"C:\\windows\\syswow64\\msiexec.exe\" in experimental wow64 mode\n"
            + "0208:err:environ:init_peb starting L\"C:\\windows\\syswow64\\rundll32.exe\" in experimental wow64 mode\n"
            + "01a0:err:msi:ITERATE_Actions Execution halted, action L\"JunoInitializeSession\" returned 1603\n"
            + "015c:err:ole:apartment_add_dll couldn't load in-process dll L\"C:\\windows\\system32\\mshtml.dll\"\n"
            + "012c:fixme:oleacc:find_class_data unhandled window class: L\"Button\"\n";

        check("ea failures: the halted action is named as Wine named it",
            EaInstallWatch.FailingAction(wineStderr) == "JunoInitializeSession"
            && EaInstallWatch.FailingActionCode(wineStderr) == 1603,
            EaInstallWatch.FailingAction(wineStderr) ?? "null");
        check("ea failures: the last halt is the one that ended it",
            EaInstallWatch.FailingAction(
                "err:msi:ITERATE_Actions Execution halted, action L\"First\" returned 1603\n"
                + "err:msi:ITERATE_Actions Execution halted, action L\"JunoInitializeSession\" returned 1603")
                == "JunoInitializeSession");
        check("ea failures: no halt line, no action and no code",
            EaInstallWatch.FailingAction(failed) is null && EaInstallWatch.FailingActionCode(failed) == 0
            && EaInstallWatch.FailingAction(null) is null && EaInstallWatch.FailingActionCode(null) == 0);

        check("ea failures: Wine's own inability to open the MSI log is recognised",
            EaInstallWatch.LogEnableFailed(wineStderr)
            && !EaInstallWatch.LogEnableFailed(failed) && !EaInstallWatch.LogEnableFailed(null));
        var wineLines = EaInstallWatch.WineDiagnosticLines(wineStderr);
        check("ea failures: the err: lines are quoted and the fixme noise is not",
            wineLines.Count == 4
            && !wineLines.Any(l => l.Contains("fixme:", StringComparison.Ordinal))
            && !wineLines.Any(l => l.Contains("oleacc", StringComparison.Ordinal))
            && !wineLines.Any(l => l.Contains("mshtml", StringComparison.Ordinal))
            && wineLines.Any(l => l.Contains("syswow64\\rundll32.exe", StringComparison.Ordinal)),
            string.Join(" | ", wineLines));
        check("ea failures: no lines to quote from nothing",
            EaInstallWatch.WineDiagnosticLines(null).Count == 0
            && EaInstallWatch.WineDiagnosticLines("").Count == 0
            && EaInstallWatch.WineDiagnosticLines(wineStderr, 0).Count == 0);

        // The cap is spent on context, never on the engine's own lines — the mistake
        // that hid this failure for one whole attempt.
        var crowded = string.Join("\n", Enumerable.Repeat(wineStderr.TrimEnd('\n'), 6));
        var capped = EaInstallWatch.WineDiagnosticLines(crowded, 2);
        check("ea failures: a crowded output drops context, not the engine's lines",
            capped.Count == 2
            && capped.All(l => l.Contains("err:msi:", StringComparison.Ordinal)),
            string.Join(" | ", capped));

        var noteNamed = EaInstallWatch.MsiFailureNote(failed, 0, wineStderr);
        check("ea failures: 1603 with Wine's reason blames EA's own action, not the handoff",
            noteNamed is not null
            && noteNamed.Contains("JunoInitializeSession", StringComparison.Ordinal)
            && noteNamed.Contains("not in the handoff", StringComparison.Ordinal)
            && noteNamed.Contains("sharing violation", StringComparison.Ordinal)
            && noteNamed.Contains("Proton", StringComparison.Ordinal)
            && !noteNamed.Contains("never opened the package", StringComparison.Ordinal),
            noteNamed ?? "null");
        var noteBlind = EaInstallWatch.MsiFailureNote(failed, 0, null);
        check("ea failures: without the child's output it says the step is unnamed, not guessed",
            noteBlind is not null
            && noteBlind.Contains("nothing in the captured output names the step", StringComparison.Ordinal)
            && !noteBlind.Contains("sharing violation", StringComparison.Ordinal)
            && !noteBlind.Contains("never opened the package", StringComparison.Ordinal),
            noteBlind ?? "null");
        var noteRead = EaInstallWatch.MsiFailureNote(failed, 4096, wineStderr);
        check("ea failures: a written MSI log is pointed at instead",
            noteRead is not null && noteRead.Contains("4096", StringComparison.Ordinal)
            && !noteRead.Contains("sharing violation", StringComparison.Ordinal), noteRead ?? "null");
        check("ea failures: no 1603, no note",
            EaInstallWatch.MsiFailureNote(apply, 0, wineStderr) is null
            && EaInstallWatch.MsiFailureNote(null, 0, null) is null);

        // The grace window's own mistake. On 2026-09-22 05:01 the installer ran
        // Burn's whole cache-and-apply cycle, failed at the 1603 and rolled back
        // in nine seconds, and the launcher's summary of it was "exited
        // immediately (code 1) — it did not start".
        var nineSeconds = EaInstallWatch.EarlyExitNote("EA installer", TimeSpan.FromSeconds(9.1), 1);
        check("ea launch: a fast exit says how long it ran, not that it never started",
            nineSeconds.Contains("9.1s", StringComparison.Ordinal)
            && nineSeconds.Contains("(code 1)", StringComparison.Ordinal)
            && !nineSeconds.Contains("did not start", StringComparison.Ordinal)
            && !nineSeconds.Contains("immediately", StringComparison.Ordinal), nineSeconds);
        check("ea launch: two fast exits are told apart by their durations",
            EaInstallWatch.EarlyExitNote("EA installer", TimeSpan.FromSeconds(9.1), 1)
            != EaInstallWatch.EarlyExitNote("EA installer", TimeSpan.FromSeconds(0.2), 1));
        check("ea launch: it claims only that the child did not stay up",
            nineSeconds.Contains("did not stay up", StringComparison.Ordinal), nineSeconds);

        // What the launcher keeps of a child's output is longer than what it
        // prints, because the diagnosis is not always in the last few lines: the
        // MSI halts and the installer's embedded browser complains for thirty more.
        var after = new List<string>(
            Enumerable.Repeat("0154:fixme:wininet:set_cookie secure not handled", 100));
        after.Add("01a0:err:msi:ITERATE_Actions Execution halted, action "
                  + "L\"JunoInitializeSession\" returned 1603");
        after.AddRange(Enumerable.Range(0, 40).Select(i =>
            $"012c:fixme:oleacc:find_class_data unhandled window class: L\"Chunk{i}\""));

        check("ea launch: the capture is longer than the printout",
            WindowsRun.CaptureLimit > 40, WindowsRun.CaptureLimit.ToString());
        var kept = EaInstallWatch.WineDiagnosticLines(string.Join('\n', after));
        check("ea launch: the diagnosis survives the noise that follows it",
            kept.Any(l => l.Contains("JunoInitializeSession", StringComparison.Ordinal)),
            string.Join(" | ", kept));
        var printed = EaInstallWatch.QuotableLines(after);
        check("ea launch: the printed tail is the newest forty lines, in order",
            printed.Count == 40 && printed[^1] == after[^1], $"{printed.Count} lines");
        check("ea launch: a ring the size of the printout would have evicted the halt line",
            !EaInstallWatch.WineDiagnosticLines(string.Join('\n', printed))
                .Any(l => l.Contains("JunoInitializeSession", StringComparison.Ordinal)));
        check("ea launch: a short capture is printed whole, and nothing is nothing",
            EaInstallWatch.QuotableLines(new[] { "a", "b" }).Count == 2
            && EaInstallWatch.QuotableLines(null).Count == 0
            && EaInstallWatch.QuotableLines(after, 0).Count == 0);
    }

    // ---------------------------------------------------------------- EA logs

    /// <summary>
    /// Finding the installer's own account. A WiX Burn bundle writes
    /// <c>EA_app_&lt;stamp&gt;.log</c> into the prefix user's Temp and dies silently
    /// otherwise, so this lookup is the only reason the failure on this machine
    /// was readable at all.
    /// </summary>
    static void EaLogs(Action<string, bool, string> report, string prefix)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        check("ea logs: no prefix, no log", ProtonLauncher.FindNewestEaLog(null) is null);
        check("ea logs: a prefix with no users has no log",
            ProtonLauncher.FindNewestEaLog(prefix) is null);

        var temp = Path.Combine(prefix, "pfx/drive_c/users/steamuser/AppData/Local/Temp");
        Directory.CreateDirectory(temp);
        var older = Path.Combine(temp, "EA_app_2020.log");
        var newer = Path.Combine(temp, "EA_app_2026.log");
        File.WriteAllText(older, "old\n");
        File.WriteAllText(newer, "line one\nline two\nline three\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        check("ea logs: the newest log wins",
            ProtonLauncher.FindNewestEaLog(prefix) == newer,
            ProtonLauncher.FindNewestEaLog(prefix) ?? "none");

        var tail = ProtonLauncher.Tail(newer, 2);
        check("ea logs: Tail returns the last lines in order",
            tail.Count == 2 && tail[0] == "line two" && tail[1] == "line three",
            string.Join(" | ", tail));
        check("ea logs: Tail of a missing file is empty",
            ProtonLauncher.Tail(Path.Combine(temp, "gone.log"), 5).Count == 0);
        check("ea logs: Tail of zero lines is empty", ProtonLauncher.Tail(newer, 0).Count == 0);

        // EA Desktop's own logs live elsewhere in the same prefix and count too.
        var eaDir = Path.Combine(prefix, "pfx/drive_c/users/steamuser/AppData/Local/Electronic Arts");
        Directory.CreateDirectory(eaDir);
        var desktopLog = Path.Combine(eaDir, "EADesktop.log");
        File.WriteAllText(desktopLog, "desktop\n");
        File.SetLastWriteTimeUtc(desktopLog, DateTime.UtcNow.AddMinutes(5));
        check("ea logs: the Desktop's own log is found too",
            ProtonLauncher.FindNewestEaLog(prefix) == desktopLog);
    }

    // -------------------------------------------------------------- shortcuts

    /// <summary>
    /// The menu entries, written into temp roots. What is asserted is the part a
    /// user cannot fix by hand: the Exec line has to carry the same environment
    /// the launcher sets in-process (Proton in a prefix knows nothing otherwise),
    /// and an entry that cannot work must be skipped rather than written.
    /// </summary>
    static void Shortcuts(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        var apps = Path.Combine(root, "applications");
        var icons = Path.Combine(root, "icons");

        check("shortcuts: the real menu is not the target",
            !DesktopShortcuts.ApplicationsDir(apps).Contains(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), StringComparison.Ordinal));

        var exec = Path.Combine(root, "bin/r5flowstate");
        var launcher = DesktopShortcuts.WriteLauncher(exec, null, apps, icons);
        check("shortcuts: the launcher entry is written", launcher.Written, launcher.Problem ?? "");
        var text = File.ReadAllText(launcher.Path);
        check("shortcuts: the entry points at the launcher", text.Contains($"Exec={exec}"), text);
        check("shortcuts: the entry is a game in the menu",
            text.Contains("Categories=Game;") && text.Contains("Terminal=false"));
        check("shortcuts: the entry is named for the product", text.Contains("Name=R5Flowstate"));
        check("shortcuts: no icon source means no icon claim",
            text.Contains("Icon=r5flowstate") && !Directory.Exists(icons));

        // With an icon, it is copied into the theme under the entry's icon name.
        var png = Path.Combine(root, "icon-mark.png");
        File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var icon = DesktopShortcuts.WriteLauncher(exec, png, apps, icons);
        var installed = Path.Combine(DesktopShortcuts.IconDir(icons), DesktopShortcuts.IconName + ".png");
        check("shortcuts: an icon is installed into the theme",
            icon.Written && File.Exists(installed)
            && File.ReadAllBytes(installed).SequenceEqual(File.ReadAllBytes(png)), installed);

        // The EA entry: skipped with a reason when there is nothing to point at.
        var prefix = Path.Combine(root, "prefix");
        var skipped = DesktopShortcuts.WriteAll(exec, string.Empty, prefix, png, apps, icons);
        var eaSkipped = skipped.Single(r => r.Name == "EA App");
        check("shortcuts: an EA entry with no EA Desktop is skipped, not written",
            !eaSkipped.Written && !File.Exists(eaSkipped.Path) && eaSkipped.Problem is not null,
            eaSkipped.Problem ?? "no reason given");

        // Point it at a real fake desktop and a real fake proton, and read the
        // command line it produces.
        var eaDir = Path.Combine(prefix, "pfx/drive_c/Program Files/Electronic Arts/EA Desktop");
        Directory.CreateDirectory(eaDir);
        var eaExe = Path.Combine(eaDir, "EADesktop.exe");
        Touch(eaExe);
        var protonDir = Path.Combine(root, "Proton - Experimental");
        Directory.CreateDirectory(protonDir);
        Touch(Path.Combine(protonDir, "proton"));

        var ea = DesktopShortcuts.WriteEaApp(protonDir, prefix, eaExe, apps, png, icons);
        check("shortcuts: the EA entry is written when EA is there", ea.Written, ea.Problem ?? "");
        var eaText = File.ReadAllText(ea.Path);
        check("shortcuts: EA starts through the same prefix Proton does",
            eaText.Contains($"STEAM_COMPAT_DATA_PATH={prefix}"), eaText);
        check("shortcuts: EA gets the launcher's own env",
            eaText.Contains("VPROJECT=1") && eaText.Contains("FROM_R5F_LAUNCHER=1"));
        check("shortcuts: a path with a space is quoted in Exec",
            eaText.Contains($"\"{Path.Combine(protonDir, "proton")}\"")
            && eaText.Contains($"\"{eaExe}\""), eaText.Split('\n').First(l => l.StartsWith("Exec=", StringComparison.Ordinal)));
        check("shortcuts: the working directory is EA's own",
            eaText.Contains("Path=" + eaDir));

        // A proton directory that does not exist is a skip, not a broken entry.
        File.Delete(ea.Path);
        var noProton = DesktopShortcuts.WriteEaApp(Path.Combine(root, "no-proton"), prefix, eaExe, apps, png, icons);
        check("shortcuts: no Proton build means no EA entry",
            !noProton.Written && !File.Exists(noProton.Path), noProton.Problem ?? "no reason given");

        check("shortcuts: quoting leaves simple paths alone",
            DesktopShortcuts.DesktopQuote("/home/x/app") == "/home/x/app");
        check("shortcuts: quoting wraps and escapes",
            DesktopShortcuts.DesktopQuote("a b\"c") == "\"a b\\\"c\"");

        // Both entries are removable, and removing twice is the same as once.
        DesktopShortcuts.WriteAll(exec, protonDir, prefix, png, apps, icons);
        DesktopShortcuts.RemoveAll(apps);
        var gone = !File.Exists(Path.Combine(DesktopShortcuts.ApplicationsDir(apps),
                        DesktopShortcuts.LauncherFileName))
                   && !File.Exists(Path.Combine(DesktopShortcuts.ApplicationsDir(apps),
                        DesktopShortcuts.EaAppFileName));
        DesktopShortcuts.RemoveAll(apps);
        check("shortcuts: removal takes both entries and repeats safely", gone);
    }

    // ----------------------------------------------------------------- helpers

    static void Touch(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, "");
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // A temp folder that will not go is not a test failure.
        }
    }
}
