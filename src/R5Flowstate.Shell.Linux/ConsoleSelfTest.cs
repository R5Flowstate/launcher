using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.Views;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Console layer, offline: the netcon wire and its id gate, the dedi output
/// classifiers and the readiness rendezvous, and the tap that turns a child's
/// streams into lines and its stdin into commands. Nothing here needs a game:
/// the netcon checks build frames rather than send them, the tap checks run
/// against throwaway /bin/sh children that print what the test tells them to,
/// and the one group that does put bytes on a socket puts a netcon host of its
/// own on the other end of it (RconHarness).
///
/// The tap checks are real processes on purpose. The tap's whole job is the
/// boundary between a line and a command, and the two rules that matter — a
/// line ends at '\n', a command is one line — can only be shown against a child
/// that reads what we write and writes back what it read.
///
/// The last group drives the launch wiring: a tap attached the way PLAY attaches
/// one, a child printing the lines that make it ready or fatal, and the window's
/// console chrome. Those checks have to pump the dispatcher themselves — the
/// suite runs as a posted job on the UI thread, which is what makes the
/// marshalling contract checkable at all (a handler that runs on the reader
/// thread would be visible here, and in a real window it would be a crash).
/// </summary>
static class ConsoleSelfTest
{
    public static void Run(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        RconChecks(c);
        GateChecks(c);
        TapChecks(c);
        LaunchChecks(c, vm, window);
        SteeringChecks(c, vm, window);
        HeartbeatChecks(c, vm, window);
        HarnessChecks(c, vm, window);
        FindChecks(c, vm, window);
        StopChecks(c, vm, window);
    }

    // ------------------------------------------------------------- netcon

    static void RconChecks(Action<string, bool, string> check)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // Upstream's own self-check: the protobuf request, the envelope, the
        // frame magic and its endianness, and the identifier gate. It is
        // portable verbatim, so it is the authority for the wire here too.
        var self = LocalRcon.SelfCheck();
        c("console rcon: upstream LocalRcon.SelfCheck is clean", self is null, self ?? "clean");

        // Ids: lowercase, digits, underscore only — the gate in front of
        // bridge_setmode, and the reason a map id can never carry a `;`.
        c("console rcon: a safe playlist id is accepted", LocalRcon.IsSafeIdentifier("survival_dev"));
        c("console rcon: a safe map id is accepted", LocalRcon.IsSafeIdentifier("mp_rr_desertlands_mu3"));
        c("console rcon: an injection attempt is refused", !LocalRcon.IsSafeIdentifier("mp_rr_x;quit"));
        c("console rcon: uppercase is refused", !LocalRcon.IsSafeIdentifier("MP_RR_X"));
        c("console rcon: an empty id is refused", !LocalRcon.IsSafeIdentifier(""));
        c("console rcon: a null id is refused", !LocalRcon.IsSafeIdentifier(null));

        var built = LocalRcon.TryBuildSetMode("survival_dev", "mp_rr_desertlands_mu3", out var cmd, out _);
        c("console rcon: setmode builds the bridge verb", built && cmd == "bridge_setmode survival_dev mp_rr_desertlands_mu3",
            cmd);
        c("console rcon: setmode refuses an injected map",
            !LocalRcon.TryBuildSetMode("survival_dev", "mp_rr_x;quit", out _, out var err) && err is not null,
            err ?? "");

        // The session: a fresh password and the loopback port the dedi is told
        // to listen on. Nothing here is ever written to argv.
        using var a = LocalRconSession.Create(37015);
        using var b = LocalRconSession.Create(37015);
        var env = a.ToEnvironment();
        c("console rcon: the session port is gamePort + 2", a.Port == 37017, $"got {a.Port}");
        c("console rcon: the session listens on loopback", a.Host == "::1", a.Host);
        using (var zero = LocalRconSession.Create(0))
        using (var reserved = LocalRconSession.Create(37014))
        {
            c("console rcon: a short game port falls back to the default port",
                zero.Port == LocalRconSession.DefaultPort, zero.Port.ToString());
            c("console rcon: game port 37014 avoids the reserved port",
                reserved.Port == LocalRconSession.DefaultPort, reserved.Port.ToString());
        }
        var pw = env[LocalRconSession.PasswordEnv];
        c("console rcon: the password is 64 lowercase hex characters",
            pw.Length == 64
            && pw.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')),
            $"{pw.Length} chars");
        c("console rcon: each session gets fresh bytes",
            !string.Equals(pw, b.ToEnvironment()[LocalRconSession.PasswordEnv], StringComparison.Ordinal));
        c("console rcon: the environment carries the launcher flag",
            env[LocalRconSession.FromLauncherEnv] == "1"
            && env[LocalRconSession.PortEnv] == a.Port.ToString(),
            $"{LocalRconSession.FromLauncherEnv}={env[LocalRconSession.FromLauncherEnv]}");

        var probe = a.Ping(TimeSpan.FromMilliseconds(400));
        c("console rcon: pinging a port nobody listens on fails, and says so",
            !probe.Ok && !string.IsNullOrEmpty(probe.Error), probe.Error ?? "(no error text)");

        a.Dispose();
        c("console rcon: a disposed session refuses to ping",
            a.Ping(TimeSpan.FromMilliseconds(200)).Error == "disposed");
        var threw = false;
        try { a.ToEnvironment(); }
        catch (ObjectDisposedException) { threw = true; }
        c("console rcon: a disposed session exposes no environment", threw);
    }

    // ------------------------------------------------- the readiness gate

    static void GateChecks(Action<string, bool, string> check)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // The tagged line, with and without the map filter.
        c("console gate: the tagged ready line is ready",
            HostReadyGate.IsReadyLine("Native(E): [R5F-HOST] ready mp_rr_x"));
        c("console gate: the ODL-replay fallback is ready",
            HostReadyGate.IsReadyLine("Native(S): [s21-dedi] ODL precache replay: 1 engine"));
        c("console gate: a ready line for another map is not ready",
            !HostReadyGate.IsReadyLine("[R5F-HOST] ready mp_lobby", "mp_rr_x"));
        c("console gate: a ready line for the wanted map is ready",
            HostReadyGate.IsReadyLine("[R5F-HOST] ready mp_rr_x", "mp_rr_x"));
        c("console gate: an empty line is not ready", !HostReadyGate.IsReadyLine(""));
        c("console gate: ANSI does not hide the ready tag",
            HostReadyGate.IsReadyLine("\u001b[38;2;1;2;3mNative(E): [R5F-HOST] ready mp_rr_x\u001b[0m"));

        // The changelevel form must name the map: it is the only line that can
        // tell map A from map B.
        c("console gate: the map-ready line matches its stem",
            HostReadyGate.IsMapReadyLine("Native(E): [R5F-HOST] ready mp_rr_arena_composite",
                "mp_rr_arena_composite"));
        c("console gate: the map-ready line rejects another stem",
            !HostReadyGate.IsMapReadyLine("[R5F-HOST] ready mp_lobby", "mp_rr_x"));
        c("console gate: the fallback line is never map-ready",
            !HostReadyGate.IsMapReadyLine("[s21-dedi] ODL precache replay: 1 engine", "mp_rr_x"));

        // Fatal: a dedi that stays alive and never hosts.
        c("console gate: an invalid level is fatal",
            HostReadyGate.IsFatalLine("CHostState::State_NewGame: Level not valid"));
        c("console gate: a starting load is not fatal",
            !HostReadyGate.IsFatalLine("Loading level: 'mp_rr_x'"));
        c("console gate: the boot teardown is not fatal",
            !HostReadyGate.IsFatalLine("Native(E):CHostState::FrameUpdate: Shutdown host game"));
        c("console gate: a script error is not by itself fatal",
            !HostReadyGate.IsFatalLine("Script(S):SCRIPT ERROR: [SERVER] missing attachment blade_base"));

        c("console gate: a script error is recognised",
            HostReadyGate.IsScriptErrorLine("Native(S): SCRIPT ERROR: [SERVER] foo"));
        c("console gate: a stack frame is not a script error",
            !HostReadyGate.IsScriptErrorLine(" -> weapon.PlayWeaponEffect( GH_SWORD_IDLE_FX_1P"));

        c("console gate: host teardown is recognised",
            HostReadyGate.IsHostShutdownLine("Native(E):CHostState::FrameUpdate: Shutdown host game"));
        c("console gate: a level load is recognised",
            HostReadyGate.IsLevelLoadLine("Loading level: 'mp_rr_x'")
            && HostReadyGate.IsLevelLoadLine("Level loaded: 'mp_rr_x'")
            && HostReadyGate.IsLevelLoadLine("---- changelevel mp_rr_x"));

        var excerpt = HostReadyGate.ScriptErrorExcerpt(
            "Script(S):SCRIPT ERROR: [SERVER] PlayWeaponParticleEffect: missing attachment blade_base");
        c("console gate: a script error excerpt drops the tag and the banner",
            excerpt == "PlayWeaponParticleEffect: missing attachment blade_base", excerpt ?? "(null)");
        c("console gate: a non-error line has no excerpt",
            HostReadyGate.ScriptErrorExcerpt("Loading level: 'mp_rr_x'") is null);

        var dialog = HostReadyGate.ClientErrorDialogText(
            "Script(U):UICodeCallback_ErrorDialog: Connection to server timed out (code:net). "
            + "See ea.com/unable-to-connect for additional information");
        c("console gate: the client disconnect text is extracted and trimmed",
            dialog == "Connection to server timed out (code:net).", dialog ?? "(null)");
        c("console gate: a non-dialog line yields no client text",
            HostReadyGate.ClientErrorDialogText("Script(U):UICodeCallback_LevelLoadingFinished:  (true)") is null);

        c("console gate: strip-ansi removes colour and keeps the text",
            ConsoleTap.StripAnsi("\u001b[38;2;10;20;30mhello\u001b[0m world") == "hello world",
            ConsoleTap.StripAnsi("\u001b[38;2;10;20;30mhello\u001b[0m world"));
        c("console gate: strip-ansi leaves plain text alone",
            ConsoleTap.StripAnsi("plain") == "plain");

        // The rendezvous. On Windows a named event crossed the process
        // boundary; here the signal comes from the line reader, so what has to
        // hold is the same set of exits.
        using (var gate = new HostReadyGate())
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            c("console gate: a cancelled wait is cancelled, not ready",
                gate.Wait(null, cts.Token) == HostReadyWait.Cancelled);
        }

        using (var gate = new HostReadyGate())
        {
            gate.SignalFromConsole();
            c("console gate: a signalled gate is ready",
                gate.Wait(null, CancellationToken.None) == HostReadyWait.Ready);
        }

        using (var gate = new HostReadyGate())
        {
            gate.SignalFatal();
            c("console gate: a fatal gate reports fatal",
                gate.Wait(null, CancellationToken.None) == HostReadyWait.Fatal);
        }

        using (var dead = Spawn("exit 0", stdin: false))
        {
            dead.WaitForExit(4000);
            using var gate = new HostReadyGate();
            c("console gate: a dead dedi ends the wait",
                gate.Wait(dead, new CancellationTokenSource(4000).Token) == HostReadyWait.ProcessDied);
        }

        // Cancellation must interrupt an in-flight wait, not be noticed only
        // before it starts: the wait blocks until something happens.
        using (var gate = new HostReadyGate())
        {
            var cts = new CancellationTokenSource();
            var result = HostReadyWait.Ready;
            var t = new Thread(() => result = gate.Wait(null, cts.Token)) { IsBackground = true };
            t.Start();
            Thread.Sleep(150);
            cts.Cancel();
            var finished = t.Join(4000);
            c("console gate: cancelling interrupts a waiting gate",
                finished && result == HostReadyWait.Cancelled, result.ToString());
        }

        using (var gate = new HostReadyGate())
        {
            gate.Dispose();
            var threw = false;
            try { gate.SignalFromConsole(); }
            catch { threw = true; }
            c("console gate: signalling a disposed gate is not an error", !threw);
        }
    }

    // ---------------------------------------------------------- the tap

    static void TapChecks(Action<string, bool, string> check)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // A child whose streams were inherited cannot be tapped: reading it
        // would yield nothing and look exactly like a quiet game.
        using (var plain = Spawn("exit 0", stdin: false, capture: false))
        {
            var threw = false;
            try { ConsoleTap.Attach(LaunchRole.Dedicated, plain); }
            catch (InvalidOperationException) { threw = true; }
            finally { try { if (!plain.HasExited) plain.Kill(); } catch { } }
            c("console tap: an uncaptured child is refused", threw);
        }

        // Lines. A trailing partial line is emitted, '\r' is dropped, and the
        // escapes reach the subscriber raw so the console can colour them.
        using (var child = Spawn("printf 'one\\ntwo\\r\\n\\033[38;2;1;2;3mthree\\033[0m'; printf 'err\\n' 1>&2",
                   stdin: false))
        {
            var lines = new ConcurrentQueue<string>();
            using var tap = ConsoleTap.Attach(LaunchRole.Dedicated, child);
            tap.LineReceived += l => lines.Enqueue(l);
            tap.Start();
            var got = WaitFor(() => lines.Count >= 4, 6000);
            var all = lines.ToArray();
            c("console tap: every line arrives", got && all.Length >= 4, string.Join(" | ", all));
            c("console tap: carriage returns are dropped",
                Array.IndexOf(all, "two") >= 0);
            c("console tap: stderr is tapped too", Array.IndexOf(all, "err") >= 0);
            c("console tap: escapes reach the subscriber raw",
                Array.Exists(all, l => l.Contains('\u001b') && ConsoleTap.StripAnsi(l) == "three"));
        }

        // A line that never ends is still delivered: 16 KB at a time.
        using (var child = Spawn("head -c 20000 /dev/zero | tr '\\0' 'x'", stdin: false))
        {
            var lines = new ConcurrentQueue<string>();
            using var tap = ConsoleTap.Attach(LaunchRole.Client, child);
            tap.LineReceived += l => lines.Enqueue(l);
            tap.Start();
            var got = WaitFor(() => lines.Count > 0, 6000);
            var first = lines.TryDequeue(out var f) ? f : "";
            c("console tap: an unterminated line is delivered in 16 KB pieces",
                got && first.Length == 16 * 1024 && first.Trim('x').Length == 0,
                $"{first.Length} chars");
        }

        // Commands. One call is one line: the newline ends it, so a smuggled
        // second command is dropped rather than queued.
        // The trailing cat keeps stdin open and drained, so the gate checks
        // below write to a pipe somebody is still reading.
        using (var child = Spawn("IFS= read -r a; IFS= read -r b; printf 'A=[%s] B=[%s]\\n' \"$a\" \"$b\"; cat > /dev/null",
                   stdin: true))
        {
            var lines = new ConcurrentQueue<string>();
            using var tap = ConsoleTap.Attach(LaunchRole.Dedicated, child);
            tap.LineReceived += l => lines.Enqueue(l);
            c("console tap: a tap with no writer yet cannot send a command",
                !tap.TryWriteCommand("before start"));
            tap.Start();
            c("console tap: a started tap can send a command", tap.CanCommand);
            c("console tap: a command with an embedded newline is cut at the newline",
                tap.TryWriteCommand("one\nEVIL"));
            c("console tap: a second command follows on its own line",
                tap.TryWriteCommand("two"));
            var got = WaitFor(() => lines.Count > 0, 6000);
            var reply = lines.TryDequeue(out var r) ? r : "";
            c("console tap: the child read exactly the two lines we wrote",
                got && reply == "A=[one] B=[two]", reply);
            // Named separately because the failure mode is invisible: a UTF-8
            // preamble on the command stream rides along inside the first
            // command and the dedi answers with a syntax error nobody can
            // explain.
            c("console tap: the first command carries no byte-order mark",
                !reply.Contains('﻿') && reply.StartsWith("A=[one]", StringComparison.Ordinal), reply);

            c("console tap: a whitespace-only command is refused", !tap.TryWriteCommand("   "));
            c("console tap: an empty command is refused", !tap.TryWriteCommand(""));
            c("console tap: an over-long command is refused", !tap.TryWriteCommand(new string('x', 513)));
            c("console tap: a 512-char command is allowed", tap.TryWriteCommand(new string('y', 512)));
            try { child.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        // Disposal stops delivery: a line that arrives afterwards is not
        // handed to a subscriber that has gone away.
        using (var child = Spawn("printf 'before\\n'; sleep 1; printf 'after\\n'", stdin: false))
        {
            var lines = new ConcurrentQueue<string>();
            var tap = ConsoleTap.Attach(LaunchRole.Client, child);
            tap.LineReceived += l => lines.Enqueue(l);
            tap.Start();
            WaitFor(() => lines.Count > 0, 6000);
            tap.Dispose();
            var arrivedBefore = lines.Count;
            Thread.Sleep(1500);
            c("console tap: a disposed tap stops delivering",
                lines.Count == arrivedBefore && lines.Contains("before"),
                $"{lines.Count} line(s)");
        }
    }

    // ------------------------------------------------------------ launch wiring

    /// <summary>
    /// A tap attached exactly the way a launch attaches one, driven by a child
    /// that prints what a dedi prints. This is the end-to-end path: child output
    /// → tap → view-model queues → the pane; and the readiness rendezvous, which
    /// on Windows was a named event and here is the dedi's own tagged line.
    /// </summary>
    static void LaunchChecks(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        c("console ui: the suite runs on the UI thread",
            Dispatcher.UIThread.CheckAccess(), "the marshalling contract");

        var openOnLaunch = vm.OpenConsoleOnLaunch;
        var tabConsole = window.FindControl<Button>("BtnTabConsole");
        var tabLocal = window.FindControl<Button>("BtnTabLocal");
        var panelConsole = window.FindControl<Control>("PanelSimpleConsole");
        var panelLocal = window.FindControl<Control>("PanelSimpleLocal");
        var btnAction = window.FindControl<Button>("BtnConsoleAction");
        var paneServer = window.FindControl<Control>("PaneConsoleServer");
        var grid = window.FindControl<Grid>("GridConsole");
        if (tabConsole is null || tabLocal is null || panelConsole is null || panelLocal is null
            || btnAction is null || paneServer is null || grid is null)
        {
            c("console chrome: the console tab's controls are all present", false, "a control is missing");
            return;
        }

        try
        {
            // --- the pane's own rule (a join to someone else's server) ---
            vm.JoinedRemote = false;
            var openAlone = vm.ConsoleServerPaneOpen && paneServer.IsVisible;
            vm.JoinedRemote = true;
            var closedOnJoin = !vm.ConsoleServerPaneOpen && !paneServer.IsVisible
                && grid.RowDefinitions[0].Height.Value == 0
                && grid.RowDefinitions[1].Height.Value == 0;
            c("console chrome: a join runs no dedi, so the server pane and its gap collapse",
                openAlone && closedOnJoin,
                $"open alone={openAlone}, collapsed on join={closedOnJoin}");

            // --- the tab, and Stop with it ---
            tabLocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var hiddenOnLocal = !btnAction.IsVisible && panelLocal.IsVisible;
            tabConsole.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var shownOnConsole = btnAction.IsVisible && panelConsole.IsVisible;
            c("console chrome: Stop follows the console tab",
                hiddenOnLocal && shownOnConsole,
                $"hidden on Local={hiddenOnLocal}, shown on Console={shownOnConsole}");

            // --- a dedi of ours: it wins over the join flag, and it is what
            //     "open the console on launch" hangs off ---
            vm.OpenConsoleOnLaunch = true;
            LaunchRole? announced = null;
            void OnAttached(LaunchRole role) => announced = role;
            vm.ConsoleAttached += OnAttached;
            try
            {
                using var child = Spawn("sleep 20", stdin: true);
                var tap = ConsoleTap.Attach(LaunchRole.Dedicated, child);
                vm.AttachConsole(LaunchRole.Dedicated, tap);

                var firstLine = WaitFor(() => vm.DrainConsole(LaunchRole.Dedicated) > 0, 4000);
                var text = vm.Lines(LaunchRole.Dedicated).FirstOrDefault()?.Text ?? "";
                c("console: attaching takes the pane over and announces the role",
                    announced == LaunchRole.Dedicated && firstLine && text.Contains("(server console)"),
                    $"announced={announced}, first line='{text}'");
                c("console chrome: a dedi of ours reopens the pane even after a join",
                    vm.JoinedRemote && vm.ConsoleServerPaneOpen && paneServer.IsVisible,
                    $"joined={vm.JoinedRemote}, pane={paneServer.IsVisible}");
                c("console chrome: the console tab comes forward on launch when asked",
                    panelConsole.IsVisible && !panelLocal.IsVisible,
                    $"console={panelConsole.IsVisible}, local={panelLocal.IsVisible}");

                // --- the rendezvous, fed by a real child's own boot lines ---
                var ready = vm.OpenHostGate() is not null;
                var wait = vm.WaitHostReadyAsync(child, CancellationToken.None);
                var waitingSeen = WaitFor(() => vm.ConsoleWaitingForHost, 4000);
                // The ready line has to name the map this dedi was told to
                // host, or a lobby boot would pass for a live match — which is
                // the gate's own rule, so the child is told the real map.
                using (var host = Spawn($"printf 'Loading level: {vm.Map}\\n'; sleep 1; "
                                        + $"printf '[R5F-HOST] ready {vm.Map}\\n'; sleep 20", stdin: false))
                {
                    var hostTap = ConsoleTap.Attach(LaunchRole.Dedicated, host);
                    vm.AttachConsole(LaunchRole.Dedicated, hostTap);
                    var settled = PumpUntil(wait, 8000);
                    c("console: a child's tagged ready line satisfies the wait — and it was a wait",
                        ready && waitingSeen && settled && wait.Result,
                        $"announced={ready}, waiting seen={waitingSeen}, settled={settled}, result={(settled ? wait.Result.ToString() : "n/a")}");
                    c("console: the wait clears its own status line",
                        !vm.ConsoleWaitingForHost, $"waiting={vm.ConsoleWaitingForHost}");
                    vm.DetachConsole(LaunchRole.Dedicated);
                }

                // --- a wait that is cancelled is not a wait that succeeded ---
                vm.OpenHostGate();
                using var idle = Spawn("sleep 20", stdin: false);
                var cancelled = new CancellationTokenSource();
                var waitCts = vm.WaitHostReadyAsync(idle, cancelled.Token);
                cancelled.CancelAfter(300);
                var settledCts = PumpUntil(waitCts, 8000);
                c("console: cancelling the wait ends it without a ready host",
                    settledCts && !waitCts.Result && !vm.ConsoleWaitingForHost,
                    $"settled={settledCts}, result={(settledCts ? waitCts.Result.ToString() : "n/a")}");

                vm.DetachConsole(LaunchRole.Dedicated);
            }
            finally
            {
                vm.ConsoleAttached -= OnAttached;
                vm.OpenConsoleOnLaunch = openOnLaunch;
            }

            // --- the reader threads: what a dedi's fault lines must and must
            //     not do. The note has to arrive on the UI thread, because its
            //     handler paints. ---
            var notes = new ConcurrentQueue<(LaunchRole Role, string Text, bool OnUi)>();
            void OnNoted(LaunchRole role, string text) =>
                notes.Enqueue((role, text, Dispatcher.UIThread.CheckAccess()));
            vm.ConsoleNoted += OnNoted;
            try
            {
                // The child reports a live level first, and that is the point:
                // Windows reads a teardown as fatal only once the host has been
                // ready (a boot that unwinds is a failed launch, which the
                // readiness wait catches instead) — the heartbeat's grace field
                // is where "has this host ever been ready" is written down, and
                // the wait's own success path is what unlocks it, so the wait is
                // driven here for real rather than assumed.
                using var faulted = Spawn(
                    $"printf '[R5F-HOST] ready {vm.Map}\\n'; sleep 1; "
                    + "printf 'SCRIPT ERROR: [SERVER] selftest boom\n'; sleep 1; "
                    + "printf 'Shutdown host game\n'; sleep 20", stdin: true);
                var tap = ConsoleTap.Attach(LaunchRole.Dedicated, faulted);
                vm.OpenHostGate();
                vm.ArmHostedMatch();
                vm.AttachConsole(LaunchRole.Dedicated, tap);
                var hostWait = vm.WaitHostReadyAsync(faulted, CancellationToken.None);
                var wasReady = PumpUntil(hostWait, 8000) && hostWait.Result;
                var noted = PumpWaitFor(() => !notes.IsEmpty, 8000);
                var fault = notes.TryDequeue(out var f) ? f : default;
                c("console: an uncaught script error plus a teardown is a fault, noted on the UI thread",
                    noted && fault.Role == LaunchRole.Dedicated
                        && fault.Text.Contains("selftest boom") && fault.OnUi,
                    $"noted={noted}, role={fault.Role}, on-ui={fault.OnUi}, text='{fault.Text}'"
                    + $", host was ready={wasReady}");
                c("console: the fault ends the match it was hosting",
                    !vm.HostedMatchArmed && !vm.HostSteeringReady,
                    $"armed={vm.HostedMatchArmed}, steering={vm.HostSteeringReady}");
                vm.DetachConsole(LaunchRole.Dedicated);

                using var disconnected = Spawn(
                    "printf 'UICodeCallback_ErrorDialog: Connection to server timed out. See ea.com for help.\n'; "
                    + "sleep 20", stdin: true);
                var clientTap = ConsoleTap.Attach(LaunchRole.Client, disconnected);
                vm.AttachConsole(LaunchRole.Client, clientTap);
                var clientNoted = PumpWaitFor(() => !notes.IsEmpty, 8000);
                var note = notes.TryDequeue(out var n) ? n : default;
                c("console: the client's own disconnect dialog is reported verbatim",
                    clientNoted && note.Role == LaunchRole.Client
                        && note.Text == "Connection to server timed out."
                        && note.OnUi,
                    $"noted={clientNoted}, on-ui={note.OnUi}, text='{note.Text}'");

                // This line was printed before the tap was attached, and the tap
                // read it with nobody subscribed until the console took over —
                // so this is the check that the ordering holds, because a line
                // read into nothing is gone, not delayed.
                vm.DrainConsole(LaunchRole.Client);
                c("console: a line the child printed before the attach is not lost",
                    vm.Lines(LaunchRole.Client).Any(l => l.Text.Contains("UICodeCallback_ErrorDialog")),
                    $"{vm.Lines(LaunchRole.Client).Count} line(s) in the client pane");
                vm.DetachConsole(LaunchRole.Client);
            }
            finally
            {
                vm.ConsoleNoted -= OnNoted;
            }
        }
        finally
        {
            vm.JoinedRemote = false;
            vm.OpenConsoleOnLaunch = openOnLaunch;
            vm.DetachConsole(LaunchRole.Dedicated);
            vm.DetachConsole(LaunchRole.Client);
            tabLocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    // ---------------------------------------------------------- steering

    /// <summary>
    /// The mode/map steering a hosted dedi gets: what the current selection means
    /// as a changelevel, when it is worth sending, and what the console's two
    /// combos are bound to. What is checked here is everything up to the wire, on
    /// purpose: this group runs with nobody listening on the loopback, so a send
    /// that went out would go nowhere. The wire itself is the harness group's job
    /// (a netcon host on the port, reading back what it was asked).
    /// </summary>
    static void SteeringChecks(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        var windowed = window as MainWindow;
        var comboMode = window.FindControl<ComboBox>("CmbConsoleMode");
        var comboMap = window.FindControl<ComboBox>("CmbConsoleMap");
        var btnReload = window.FindControl<Button>("BtnReloadLevel");
        var panelConsole = window.FindControl<Control>("PanelSimpleConsole");
        if (windowed is null || comboMode is null || comboMap is null || btnReload is null
            || panelConsole is null)
        {
            c("console steering: the console tab's pickers are all present", false, "a control is missing");
            return;
        }

        static bool IsLobbyCard(ModeCardViewModel x) =>
            ModeCardViewModel.IsLobbyPlaylist(x.Id)
            || (x.MapIsPinned && x.SelectedMapStem.Contains("lobby", StringComparison.OrdinalIgnoreCase));

        var lobby = vm.ModeCards.FirstOrDefault(IsLobbyCard);
        var real = vm.ModeCards.FirstOrDefault(x => !IsLobbyCard(x) && x.Maps.Count > 0);
        var restore = vm.SelectedMode;

        // The player's own settings file, so it can be put back exactly as it
        // was. Two of these checks drive controls whose handlers persist what
        // they write — the mode combo through SelectModeCard, the map combo
        // through the window's own handler — and there is no way to exercise
        // those paths without their saving. So the file is read here and
        // rewritten byte for byte at the end: a suite that runs on the player's
        // own machine has no business leaving a rail position behind.
        var settingsPath = LinuxSettings.ConfigPath;
        var settingsBefore = File.Exists(settingsPath)
            ? File.ReadAllText(settingsPath)
            : null;

        // The art lane is answered from the seam while this group runs, and that is
        // not tidiness either. Both lanes write to the same status bar, and two of
        // these checks read it: "Reload with no host is a no-op" compares the status
        // before and after, and a reload that refreshes the hero supersedes whatever
        // decode was in flight — whose "replaced by a newer request" then lands
        // inside the measurement and is read as the reload's own answer. That only
        // started happening once the art host could actually run, which is exactly
        // the kind of coupling a hermetic check must not have. A batch with no note
        // and no outcomes writes nothing, so what the status shows is the steering
        // and nothing else. The art lane is checked, with this same seam and real
        // decode notes, by the hero group in SelfTestRunner.
        var realBatch = MainViewModel.BatchOverride;
        MainViewModel.BatchOverride = (_, _) => Task.FromResult(
            new ArtBatch(Array.Empty<ArtOutcome>(), false, 0, string.Empty, string.Empty));

        try
        {
            // --- what the selection means as a changelevel ---
            if (real is not null)
            {
                vm.SelectModeCard(real, persist: false);
                var ok = vm.TrySelectedChangePair(out var playlist, out var map);
                c("console steering: the pair is the selected card's own",
                    ok && playlist == real.PlaylistId && map == real.SelectedMapStem,
                    $"{(ok ? playlist + " " + map : "refused")} vs card {real.PlaylistId} {real.SelectedMapStem}");
            }

            if (lobby is not null)
            {
                vm.SelectModeCard(lobby, persist: false);
                c("console steering: a lobby is not a level to drop into, so it is refused",
                    !vm.TrySelectedChangePair(out var lp, out var lm),
                    $"refused {lp} {lm}");
            }

            // --- nothing is sent to nobody ---
            vm.SelectModeCard(real ?? vm.ModeCards[0], persist: false);
            vm.DropLocalRcon();
            c("console steering: with no session there is nothing to change and nothing to reload",
                !vm.HostSteeringReady && !vm.ChangeMapPending() && !vm.ChangeMapBusy,
                $"ready={vm.HostSteeringReady}, pending={vm.ChangeMapPending()}");

            var before = vm.SimpleStatus;
            var noop = vm.ReloadLevelAsync();
            var settled = PumpUntil(noop, 4000);
            c("console steering: Reload with no host is a no-op, not an error",
                settled && vm.SimpleStatus == before && !vm.ChangeMapBusy,
                $"status='{vm.SimpleStatus}'");

            // --- a session, a dedi of ours, and a pick that differs ---
            using var child = Spawn("sleep 20", stdin: false);
            var tap = ConsoleTap.Attach(LaunchRole.Dedicated, child);
            vm.AttachConsole(LaunchRole.Dedicated, tap);
            try
            {
                var session = vm.OpenRcon(37015);
                c("console steering: the session is minted for the game port it will steer",
                    vm.HostSteeringReady && session.Port == 37017,
                    $"ready={vm.HostSteeringReady}, port={session.Port}");

                // Attaching the tap announces itself on the dispatcher, and the
                // pending rule reads that liveness -- so let it land first.
                var alive = PumpWaitFor(() => vm.ServerConsoleLive, 4000);

                if (real is not null)
                {
                    vm.SelectModeCard(real, persist: false);
                    // A level this card is not on: the rule is "the pick differs
                    // from what is live", and the live pair is what it is told.
                    vm.SetLivePair("survival", "mp_rr_olympus_mu2");
                    var pending = vm.ChangeMapPending();
                    vm.SetLivePair(real.PlaylistId, real.SelectedMapStem);
                    var after = vm.ChangeMapPending();
                    c("console steering: a pick that differs from the live level is worth sending",
                        alive && pending && !after,
                        $"alive={alive}, pending={pending}, then live={after}");
                }

                // --- the combos are the rail in combo form ---
                panelConsole.IsVisible = true;
                windowed.RefreshConsoleChrome();
                var card = vm.SelectedMode;
                c("console steering: the mode combo offers the rail's cards and shows the selected one",
                    card is not null
                    && ReferenceEquals(comboMode.ItemsSource, vm.ModeCards)
                    && ReferenceEquals(comboMode.SelectedItem, card)
                    && comboMode.IsVisible,
                    $"{comboMode.ItemCount} item(s), selected='{(comboMode.SelectedItem as ModeCardViewModel)?.Id}'");

                c("console steering: the map combo is the selected mode's maps",
                    card is not null
                    && ReferenceEquals(comboMap.ItemsSource, card.Maps)
                    && ReferenceEquals(comboMap.SelectedItem, card.SelectedMap)
                    && comboMap.IsVisible == (!card.MapIsPinned && card.Maps.Count > 1),
                    $"{card?.Maps.Count} map(s), pinned={card?.MapIsPinned}, visible={comboMap.IsVisible}");

                c("console steering: Reload is offered only for a host this launcher steers",
                    btnReload.IsVisible && btnReload.IsEnabled,
                    $"visible={btnReload.IsVisible}, enabled={btnReload.IsEnabled}");

                // --- the combo writes the mode's map, the way the tile grid does ---
                // This drives the window's own handler, and that handler persists
                // -- so the mode is put back on the map it was on and the store is
                // re-saved from the restored rail in the finally below. The
                // player's settings file must read the same after the suite as it
                // did before, and ModeMaps entries are the only thing the suite
                // can leave behind: a card that had no remembered map before gets
                // one, which is what selecting that mode once would do anyway.
                if (card is not null && card.Maps.Count > 1)
                {
                    var was = card.SelectedMap;
                    var pick = card.Maps[1];
                    comboMap.SelectedItem = pick;
                    c("console steering: the map combo moves the mode, and the hero follows",
                        ReferenceEquals(card.SelectedMap, pick)
                        && vm.HeroCommand.Contains(pick.Stem, StringComparison.Ordinal),
                        $"{card.SelectedMapStem} / '{vm.HeroCommand}'");
                    card.SelectedMap = was;
                }

                // --- and the session dies with the host ---
                vm.DropLocalRcon();
                c("console steering: dropping the session forgets the live level with it",
                    !vm.HostSteeringReady && vm.LivePlaylist is null && vm.LiveMap is null,
                    $"ready={vm.HostSteeringReady}, live={vm.LivePlaylist}/{vm.LiveMap}");
            }
            finally
            {
                vm.DetachConsole(LaunchRole.Dedicated);
                vm.DropLocalRcon();
                panelConsole.IsVisible = false;
                if (restore is not null)
                    vm.SelectModeCard(restore, persist: false);

                // The rail is back where it was, and so is the file: everything
                // the checks themselves changed is undone above, and this puts
                // the settings back exactly as they were found (the store is
                // only read again on the next launch, so nothing in flight cares).
                if (settingsBefore is not null)
                {
                    try { File.WriteAllText(settingsPath, settingsBefore); }
                    catch { /* the suite does not fail a check over its own cleanup */ }
                }
            }
        }
        catch (Exception ex)
        {
            c("console steering: the checks ran", false, ex.Message);
        }
        finally
        {
            // The art lane is the suite's own caller's to answer, not this group's.
            MainViewModel.BatchOverride = realBatch;
        }
    }

    // ---------------------------------------------------------- host heartbeat

    /// <summary>
    /// The watchdog that asks a live dedi — over netcon, which is the only way
    /// it can be asked anything — whether it is still answering. The pings
    /// themselves are not waited for: the rule is, and the rule is stated with
    /// the clock passed in, because the real spacing is ten seconds a ping and
    /// eighty for the limit.
    ///
    /// What is driven for real: the window's tick source, the readiness wait
    /// that unlocks the grace (the heartbeat's own trigger), one ping that goes
    /// out and comes back unanswered, and the miss counter reaching its limit,
    /// which is the one case that has to do something rather than count. The
    /// other end of that ping — a host that answers it — belongs to the harness
    /// group below, which is the one group with a listener to answer with.
    ///
    /// Not driven here, and why: the change-in-flight clause. Suppressing pings
    /// while a mode or map change is mid-send is the same pair
    /// <c>ApplyModeAsync</c> sets, and reaching it means the eight-second retry
    /// a real send performs against a server that is not listening — eight
    /// seconds of the suite for a clause that reads two fields.
    /// </summary>
    static void HeartbeatChecks(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        var windowed = window as MainWindow;
        if (windowed is null)
        {
            c("host heartbeat: the window is the one the launcher builds", false, "not a MainWindow");
            return;
        }

        var notes = new ConcurrentQueue<string>();
        void OnNoted(LaunchRole role, string text)
        {
            if (role == LaunchRole.Dedicated)
                notes.Enqueue(text);
        }

        vm.WindowClosing = false;
        vm.DisarmHostedMatch();
        vm.DropLocalRcon();
        var before = vm.SimpleStatus;

        try
        {
            // --- the tick source, and what a tick does with no match ---
            c("host heartbeat: the window owns a clock for it",
                windowed.ProcWatchRunning,
                $"proc watch running={windowed.ProcWatchRunning}");
            c("host heartbeat: a tick with no match of ours asks nothing",
                !vm.HostHeartbeatDue(DateTime.UtcNow.AddDays(1)) && vm.HostHeartbeatMisses == 0,
                $"due={vm.HostHeartbeatDue(DateTime.UtcNow.AddDays(1))}, misses={vm.HostHeartbeatMisses}");
            vm.TickHostHeartbeat(DateTime.UtcNow.AddDays(1));
            c("host heartbeat: and it notes nothing, however overdue",
                vm.HostHeartbeatMisses == 0 && vm.SimpleStatus == before,
                $"misses={vm.HostHeartbeatMisses}");

            // --- a match of ours, whose host has not reported a level yet ---
            vm.ArmHostedMatch();
            vm.OpenRcon(vm.Port);
            var neverReady = DateTime.UtcNow.AddDays(1);
            c("host heartbeat: a dedi that has not reported a level is never asked",
                vm.HostedMatchArmed && !vm.HostHeartbeatDue(neverReady),
                $"armed={vm.HostedMatchArmed}, due={vm.HostHeartbeatDue(neverReady)}");

            // --- the host says it is up, which is what unlocks the grace.
            //     A fresh rendezvous, because a gate that was signalled fatal
            //     stays fatal (which is what the check above left behind). ---
            vm.OpenHostGate();
            using var host = Spawn($"printf '[R5F-HOST] ready {vm.Map}\\n'; sleep 40", stdin: false);
            var tap = ConsoleTap.Attach(LaunchRole.Dedicated, host);
            vm.AttachConsole(LaunchRole.Dedicated, tap);
            var hostWait = vm.WaitHostReadyAsync(host, CancellationToken.None);
            var ready = PumpUntil(hostWait, 8000) && hostWait.Result;
            var readyAt = DateTime.UtcNow;
            var withinGrace = !vm.HostHeartbeatDue(readyAt.AddSeconds(5));
            var pastGrace = vm.HostHeartbeatDue(readyAt.AddSeconds(20));
            c("host heartbeat: the first ping waits out the grace, and only then goes",
                ready && withinGrace && pastGrace,
                $"ready={ready}, inside the grace={withinGrace}, past it={pastGrace}");
            c("host heartbeat: the spacing is the rule, not the timer",
                !vm.HostHeartbeatDue(readyAt.AddSeconds(22)) && vm.HostHeartbeatDue(readyAt.AddSeconds(30)),
                $"22 s due=False, 30 s due={vm.HostHeartbeatDue(readyAt.AddSeconds(30))}");

            // --- the ping itself: a tick that goes out and comes back unanswered
            //     (this session's port has nobody listening, which is what an
            //     unanswered ping looks like). The clock is the same one the two
            //     checks above advanced, so the ping is due when it is sent. ---
            vm.TickHostHeartbeat(readyAt.AddSeconds(40));
            var counted = PumpWaitFor(() => vm.HostHeartbeatMisses == 1, 6000);
            c("host heartbeat: a tick really asks the dedi, and an unanswered ping is a miss",
                counted,
                $"misses={vm.HostHeartbeatMisses}");

            // --- an answer resets the count ---
            vm.NoteHostHeartbeat(true, DateTime.UtcNow);
            c("host heartbeat: one answer and the count starts over",
                vm.HostHeartbeatMisses == 0, $"misses={vm.HostHeartbeatMisses}");

            // --- seven misses are not a verdict ---
            vm.ConsoleNoted += OnNoted;
            var quiet = vm.SimpleStatus;
            for (var i = 0; i < 7; i++)
                vm.NoteHostHeartbeat(false, DateTime.UtcNow);
            c("host heartbeat: seven unanswered pings are still not a hung server",
                vm.HostedMatchArmed && notes.IsEmpty && vm.SimpleStatus == quiet,
                $"misses={vm.HostHeartbeatMisses}, armed={vm.HostedMatchArmed}, "
                + $"notes={notes.Count}, status='{vm.SimpleStatus}'");

            // --- the eighth is ---
            vm.NoteHostHeartbeat(false, DateTime.UtcNow);
            Dispatcher.UIThread.RunJobs();
            var hungNote = notes.TryDequeue(out var n) ? n : "";
            c("host heartbeat: the eighth miss is a hung server, and it is reported",
                !string.IsNullOrEmpty(hungNote)
                && string.Equals(vm.SimpleStatus, Loc.Get("status_server_hung"), StringComparison.Ordinal),
                $"note='{hungNote}', status='{vm.SimpleStatus}'");
            c("host heartbeat: and the match it was watching is over",
                !vm.HostedMatchArmed && !vm.HostSteeringReady && !vm.ServerConsoleLive,
                $"armed={vm.HostedMatchArmed}, steering={vm.HostSteeringReady}, pane live={vm.ServerConsoleLive}");

            // --- one fault per match (Windows' _crashPromptPosted), which is not
            //     the same clause as the armed gate above: a client-lost fault
            //     leaves the match running, so this one is stated with the match
            //     still armed. Arming again is what makes it a new match. ---
            vm.ArmHostedMatch();
            vm.QueueHostedServerFault(MainViewModel.HostedServerFault.ClientLost, "selftest");
            Dispatcher.UIThread.RunJobs();
            var firstFault = notes.TryDequeue(out var firstNote);
            var staysArmed = vm.HostedMatchArmed;
            vm.QueueHostedServerFault(MainViewModel.HostedServerFault.ClientLost, "selftest");
            Dispatcher.UIThread.RunJobs();
            c("host heartbeat: a match reports one fault, however many say so",
                firstFault && staysArmed && notes.IsEmpty,
                $"first='{firstNote}', still armed={staysArmed}, then {notes.Count} more");
            vm.DisarmHostedMatch();
        }
        catch (Exception ex)
        {
            c("host heartbeat: the checks ran", false, ex.Message);
        }
        finally
        {
            vm.ConsoleNoted -= OnNoted;
            vm.DetachConsole(LaunchRole.Dedicated);
            vm.DropLocalRcon();
            vm.DisarmHostedMatch();
        }
    }

    // ---------------------------------------------- a netcon host to talk to

    /// <summary>
    /// The three things the groups above cannot reach, driven against a real
    /// listener: a ping that comes back <em>answered</em>, a ping the host
    /// refuses, and the steering's <c>bridge_setmode</c> arriving on the wire.
    /// A send is only a send if something receives it, so this group puts a
    /// netcon host on the port the launcher dials — <see cref="RconHarness"/> —
    /// and reads back what it was asked.
    ///
    /// The listener is also the one place the whole changelevel round trip can be
    /// closed: the pick, the command it becomes, and the dedi's own ready line
    /// landing it. The last part runs the other way round from the wire — a child
    /// printing the ready line, tapped the way a real dedi is — because that line
    /// is what the launcher treats as the level being live.
    ///
    /// This group runs after the two before it on purpose: they state "an
    /// unanswered ping is a miss" against this same port, and that reading needs
    /// nobody on it.
    /// </summary>
    static void HarnessChecks(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        var restore = vm.SelectedMode;
        vm.WindowClosing = false;
        vm.DisarmHostedMatch();
        vm.DropLocalRcon();

        RconHarness? host = null;
        try
        {
            var session = vm.OpenRcon(vm.Port);
            host = new RconHarness(session.Port);

            // --- a host that answers is a host the launcher can see ---
            var env = session.ToEnvironment();
            var ping = session.Ping(TimeSpan.FromSeconds(2));
            var sameSecret = env.TryGetValue(LocalRconSession.PasswordEnv, out var wanted)
                && string.Equals(host.LastPassword, wanted, StringComparison.Ordinal);
            c("console harness: a host that answers is a host the launcher can see",
                ping.Ok && sameSecret,
                $"ok={ping.Ok}, error='{ping.Error}', password on the wire is the "
                + $"environment's={sameSecret}");

            // --- a host that answers back "no" is refused, not assumed ---
            host.RefuseAuth = true;
            var refused = session.Ping(TimeSpan.FromSeconds(2));
            host.RefuseAuth = false;
            c("console harness: a host that refuses the password is refused",
                !refused.Ok,
                $"ok={refused.Ok}, error='{refused.Error}'");

            // --- and the hung frame loop: accepts, never answers ---
            host.Silent = true;
            var silent = session.Ping(TimeSpan.FromSeconds(1.5));
            host.Silent = false;
            c("console harness: a host that accepts and never answers is a miss, not a refusal",
                !silent.Ok && !string.IsNullOrEmpty(silent.Error),
                $"ok={silent.Ok}, error='{silent.Error}'");

            // --- the send only the harness can receive ---
            var card = vm.ModeCards.FirstOrDefault(x =>
                !ModeCardViewModel.IsLobbyPlaylist(x.Id)
                && !(x.MapIsPinned && x.SelectedMapStem.Contains("lobby", StringComparison.OrdinalIgnoreCase))
                && x.Maps.Count > 0);
            if (card is null)
            {
                c("console harness: there is a level to steer to", false, "no live-mode card");
                return;
            }

            vm.SelectModeCard(card, persist: false);
            // The verb and the line the dedi's console is handed: Windows sends
            // the command as its own line, so the verb names it and the line is
            // the whole thing (LocalRcon.TryBuildSetMode's output).
            var wantLine = "bridge_setmode " + card.PlaylistId + " " + card.SelectedMapStem;
            var change = vm.ChangeMapAsync();
            var sent = PumpUntil(change, 8000);
            Dispatcher.UIThread.RunJobs();
            var asked = host.Commands;
            var last = asked.Length > 0 ? asked[^1] : ("", "");
            c("console harness: the steering's bridge_setmode goes out on the wire",
                sent && last.Item1 == "bridge_setmode"
                && string.Equals(last.Item2, wantLine, StringComparison.Ordinal),
                $"asked='{last.Item2}' as verb '{last.Item1}', wanted='{wantLine}'");

            // --- and the dedi's own ready line is what makes it live ---
            var changing = vm.SimpleStatus;
            using (var mapHost = Spawn(
                $"printf '[R5F-HOST] ready {card.SelectedMapStem}\\n'; sleep 20", stdin: false))
            {
                vm.AttachConsole(LaunchRole.Dedicated, ConsoleTap.Attach(LaunchRole.Dedicated, mapHost));
                // The name the launcher prints is the pick's own either way --
                // the tile's when the console is steering a card's map, the
                // catalogue's when it is not -- so both are accepted here.
                var tile = card.SelectedMap;
                var playing = Loc.Format("status_playing_map",
                    tile?.DisplayName ?? card.SelectedMapStem);
                var playingByName = Loc.Format("status_playing_map",
                    MapLabels.PlayerName(card.SelectedMapStem, vm.CatalogMapNames));
                var landed = PumpWaitFor(() => string.Equals(vm.SimpleStatus, playing, StringComparison.Ordinal)
                    || string.Equals(vm.SimpleStatus, playingByName, StringComparison.Ordinal), 8000);
                c("console harness: the dedi's own ready line is what lands the change",
                    landed && !vm.ChangeMapBusy
                    && string.Equals(vm.LiveMap, card.SelectedMapStem, StringComparison.OrdinalIgnoreCase),
                    $"was '{changing}', now '{vm.SimpleStatus}' (wanted '{playing}'), "
                    + $"live={vm.LivePlaylist}/{vm.LiveMap}");
            }
            vm.DetachConsole(LaunchRole.Dedicated);

            // --- the heartbeat, with a host on the other end this time. The
            //     grace is unlocked the way a launch unlocks it: the readiness
            //     wait's own Ready exit, so the tick below has a ping to send. ---
            vm.ArmHostedMatch();
            vm.OpenHostGate();
            using var rconHost = Spawn($"printf '[R5F-HOST] ready {card.SelectedMapStem}\\n'; sleep 40",
                stdin: false);
            vm.AttachConsole(LaunchRole.Dedicated, ConsoleTap.Attach(LaunchRole.Dedicated, rconHost));
            var hostWait = vm.WaitHostReadyAsync(rconHost, CancellationToken.None);
            var ready = PumpUntil(hostWait, 8000) && hostWait.Result;
            // The clock is stated well past the grace (which the readiness wait
            // above has just set to fifteen seconds from now) and past whatever
            // the group before this one left in the ten-second spacing, because
            // that spacing is real wall-clock bookkeeping the groups share.
            var t0 = DateTime.UtcNow.AddMinutes(5);
            vm.NoteHostHeartbeat(false, t0);
            vm.NoteHostHeartbeat(false, t0);
            vm.TickHostHeartbeat(t0.AddSeconds(20));
            var cleared = PumpWaitFor(() => vm.HostHeartbeatMisses == 0, 8000);
            c("console harness: a tick's ping to an answering host clears the misses",
                ready && cleared && vm.HostedMatchArmed,
                $"ready={ready}, misses={vm.HostHeartbeatMisses}, armed={vm.HostedMatchArmed}");

            // --- and the same tick against a host that has stopped answering is
            //     counted, not yet a fault: the limit is what faults. ---
            host.Silent = true;
            var before = vm.HostHeartbeatMisses;
            vm.TickHostHeartbeat(t0.AddSeconds(40));
            var missed = PumpWaitFor(() => vm.HostHeartbeatMisses == before + 1, 8000);
            c("console harness: a ping the host never answers is the miss the limit counts",
                missed && vm.HostedMatchArmed && vm.HostHeartbeatMisses < 8,
                $"misses={vm.HostHeartbeatMisses}, armed={vm.HostedMatchArmed}");
            host.Silent = false;
        }
        catch (Exception ex)
        {
            c("console harness: the checks ran", false, ex.Message);
        }
        finally
        {
            host?.Dispose();
            vm.DetachConsole(LaunchRole.Dedicated);
            vm.DropLocalRcon();
            vm.DisarmHostedMatch();
            if (restore is not null)
                vm.SelectModeCard(restore, persist: false);
        }
    }

    // --------------------------------------------------------------- find bar


    /// <summary>
    /// Windows' console find: a bar per pane that counts the hits in that pane's
    /// own text, steps through them with wrap-around, paints the current one and
    /// re-finds when the log grows. The first half drives the view model, where
    /// the matching rules live; the second drives the same path through the real
    /// controls — the box, the buttons, the case chip, the window's Ctrl+F and a
    /// pointer press in the pane — because the tags, the anchors and the paint
    /// are what a find bar usually gets wrong.
    ///
    /// The harness is a real enough one to wait on: the console timer's own ticks
    /// arrive during these pumps (that is why the phase of
    /// <c>ConsoleFindTick</c> is shared and the rule below is stated per four
    /// consecutive ticks), and a TextBox hands its TextChanged to the next
    /// dispatcher pass rather than to the setter, which is why every keystroke
    /// here is followed by a pump.
    /// </summary>
    static void FindChecks(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // The window's own tick: the console timer calls it too, but a check
        // cannot wait on a 250 ms timer to land where it wants it, so it calls
        // it where a real window would.
        var win = (MainWindow)window;
        var tabConsole = window.FindControl<Button>("BtnTabConsole");
        var tabLocal = window.FindControl<Button>("BtnTabLocal");
        var bar = window.FindControl<Control>("BarFindServer");
        var input = window.FindControl<TextBox>("TxtFindServer");
        var counter = window.FindControl<TextBlock>("TxtFindServerCount");
        var caseToggle = window.FindControl<CheckBox>("ChkFindServerCase");
        var openButton = window.FindControl<Button>("BtnConsoleFind");
        var nextButton = window.FindControl<Button>("BtnFindServerNext");
        var prevButton = window.FindControl<Button>("BtnFindServerPrev");
        var closeButton = window.FindControl<Button>("BtnFindServerClose");
        var clientBar = window.FindControl<Control>("BarFindClient");
        var clientInput = window.FindControl<TextBox>("TxtFindClient");
        var clientCounter = window.FindControl<TextBlock>("TxtFindClientCount");
        var pane = window.FindControl<ConsolePane>("TxtConsoleServer");
        var clientPane = window.FindControl<ConsolePane>("TxtConsoleClient");
        if (tabConsole is null || tabLocal is null || bar is null || input is null || counter is null
            || caseToggle is null || openButton is null || nextButton is null || prevButton is null
            || closeButton is null || clientBar is null || clientInput is null || clientCounter is null
            || pane is null || clientPane is null)
        {
            c("console find: the find bar's controls are all present", false, "a control is missing");
            return;
        }

        var corpus = new[] { "hello world", "the WORLD is quiet", "nothing here", "world world", "alpha", "beta" };
        var follows = pane.Follow;
        var clientFollows = clientPane.Follow;

        try
        {
            vm.JoinedRemote = false;
            Seed(vm, LaunchRole.Dedicated, corpus);
            Seed(vm, LaunchRole.Client, corpus);

            // --- the matching rules ---
            var insensitive = vm.RebuildFind(LaunchRole.Dedicated, "world", false, 0);
            var insensitiveLines = vm.FindMatches(LaunchRole.Dedicated).Select(m => m.Line).ToArray();
            var sensitive = vm.RebuildFind(LaunchRole.Dedicated, "world", true, 0);
            c("console find: a query finds every hit, ignoring case by default",
                insensitive.Count == 4, $"{insensitive.Count} hit(s)");
            c("console find: two hits on one line are two matches",
                insensitiveLines.Length == 4 && insensitiveLines.Count(l => l == 3) == 2,
                $"lines {string.Join(",", insensitiveLines)}");
            c("console find: match case narrows to the exact spelling",
                sensitive.Count == 3 && sensitive.Counter == "1/3",
                $"{sensitive.Count} hit(s), counter '{sensitive.Counter}' (the WORLD on line 1 drops out)");

            var acrossLines = vm.RebuildFind(LaunchRole.Dedicated, "alpha beta", false, 0);
            c("console find: a match never crosses a line break",
                acrossLines.Count == 0 && acrossLines.Counter == Loc.Get("find_none"),
                $"{acrossLines.Count} hit(s), counter '{acrossLines.Counter}'");
            var noQuery = vm.RebuildFind(LaunchRole.Dedicated, "", false, 0);
            c("console find: an empty query counts nothing and says nothing",
                noQuery.Count == 0 && noQuery.Counter.Length == 0, $"counter '{noQuery.Counter}'");

            // --- stepping, and the two anchors ---
            vm.RebuildFind(LaunchRole.Dedicated, "world", false, 0);
            var atFirst = vm.FindCurrent(LaunchRole.Dedicated);
            var second = vm.StepFind(LaunchRole.Dedicated, 1, MatchLine(vm, LaunchRole.Dedicated));
            c("console find: stepping moves one match at a time",
                atFirst == 0 && second.Current == 1, $"first={atFirst}, next={second.Current}");

            // A following pane hands over the tail as its anchor, so a fresh
            // query lands on the newest match rather than the oldest.
            var atTail = vm.RebuildFind(LaunchRole.Dedicated, "world", false,
                MainViewModel.ConsoleFindTailAnchor);
            c("console find: a following pane anchors at the tail, so the newest match wins",
                atTail.Current == atTail.Count - 1 && atTail.Counter == $"{atTail.Count}/{atTail.Count}",
                $"current={atTail.Current}, counter '{atTail.Counter}'");
            var wrapped = vm.StepFind(LaunchRole.Dedicated, 1, MainViewModel.ConsoleFindTailAnchor);
            c("console find: stepping past the last match wraps to the first",
                wrapped.Current == 0, $"current={wrapped.Current}");

            // ...and Windows' rule for an anchor beyond the last line: not the
            // tail, so the walk wraps to the first match.
            var past = vm.RebuildFind(LaunchRole.Dedicated, "world", false, int.MaxValue - 1);
            c("console find: an anchor past the last line wraps to the first match",
                past.Current == 0, $"current={past.Current} of {past.Count}");

            // --- a log that grew under the matches ---
            vm.AppendConsoleLine(LaunchRole.Dedicated, "a new world arrives");
            vm.StepFind(LaunchRole.Dedicated, 1, 0);
            var stale = vm.FindMatches(LaunchRole.Dedicated).Count;
            vm.DrainConsole(LaunchRole.Dedicated);
            var rebuilt = vm.StepFind(LaunchRole.Dedicated, 1, 0);
            c("console find: matches are rebuilt once the pane has taken lines",
                stale == 4 && rebuilt.Count == 5,
                $"before the drain={stale}, after={rebuilt.Count}");

            // --- the cap ---
            Seed(vm, LaunchRole.Dedicated, new[] { string.Concat(Enumerable.Repeat("ab", 10001)) });
            var capped = vm.RebuildFind(LaunchRole.Dedicated, "ab", false, 0);
            c("console find: the walk stops at the cap and says so",
                capped.Count == MainViewModel.ConsoleFindMaxMatches && capped.Capped
                && capped.Counter.EndsWith("+", StringComparison.Ordinal)
                && capped.Counter.Contains($"{MainViewModel.ConsoleFindMaxMatches}+", StringComparison.Ordinal),
                $"{capped.Count} hit(s), counter '{capped.Counter}'");

            // --- what a cleared pane keeps and what it forgets ---
            Seed(vm, LaunchRole.Dedicated, corpus);
            c("console find: clearing a pane forgets its matches and keeps the player's query",
                vm.FindMatches(LaunchRole.Dedicated).Count == 0
                && vm.FindQuery(LaunchRole.Dedicated) == "ab",
                $"{vm.FindMatches(LaunchRole.Dedicated).Count} stale match(es), query '{vm.FindQuery(LaunchRole.Dedicated)}'");

            // --- the same path, through the controls the player touches ---
            tabConsole.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            c("console find: the toolbar button opens the pane's bar", bar.IsVisible,
                $"bar visible={bar.IsVisible}");

            input.Text = "world";
            Dispatcher.UIThread.RunJobs();
            c("console find: the box is debounced — the keystroke alone counts nothing",
                vm.FindQuery(LaunchRole.Dedicated).Length == 0 && string.IsNullOrEmpty(counter.Text),
                $"query '{vm.FindQuery(LaunchRole.Dedicated)}', counter '{counter.Text}'");

            // Enter runs what the debounce has not: the 150 ms timer is the one
            // thing here that does not arrive during a pump (the console timer's
            // ticks do), so the check drives the flush a player gets by pressing
            // the key, and the debounce is checked by what it has not done yet.
            var enter = Stroke(Key.Enter);
            input.RaiseEvent(enter);
            var counted = PumpLayout(window, () => counter.Text == "4/4", 3000);
            var firstPaint = PumpLayout(window, () => pane.PaintedLine >= 0, 3000);
            c("console find: Enter counts the hits and paints the newest match",
                counted && firstPaint && enter.Handled
                && pane.PaintedLine == 3 && pane.PaintedText == "world",
                $"counter '{counter.Text}', painted line {pane.PaintedLine} '{pane.PaintedText}'");

            input.RaiseEvent(Stroke(Key.Enter));
            var stepped = PumpLayout(window, () => pane.PaintedLine == 0, 3000);
            c("console find: Enter again steps on, and the highlight goes with it",
                stepped && counter.Text == "1/4" && pane.PaintedText == "world",
                $"counter '{counter.Text}', painted line {pane.PaintedLine}");

            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var nextPainted = PumpLayout(window, () => pane.PaintedLine == 1, 3000);
            c("console find: the next button paints the match after the current one",
                nextPainted && counter.Text == "2/4" && pane.PaintedText == "WORLD",
                $"counter '{counter.Text}', painted line {pane.PaintedLine}");
            prevButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var prevPainted = PumpLayout(window, () => pane.PaintedLine == 0, 3000);
            c("console find: the previous button paints the one before it",
                prevPainted && counter.Text == "1/4", $"counter '{counter.Text}'");

            // --- the tick rule, stated the way the timer means it ---
            // The phase is shared with the console timer's own ticks, which land
            // inside these pumps, so the check is the rule itself: one re-find in
            // any four consecutive ticks.
            pane.Follow = true;
            vm.AppendConsoleLine(LaunchRole.Dedicated, "a new world arrives");
            vm.DrainConsole(LaunchRole.Dedicated);
            var before = counter.Text;
            var refoundAt = 0;
            for (var i = 1; i <= 4 && refoundAt == 0; i++)
            {
                win.ConsoleFindTick();
                if (counter.Text != before)
                    refoundAt = i;
            }
            c("console find: four consecutive ticks re-find exactly once",
                refoundAt is >= 1 and <= 4,
                $"re-found on tick {refoundAt} of four, counter '{counter.Text}'");

            var followed = PumpLayout(window, () => pane.PaintedLine == 6, 3000);
            c("console find: a following pane takes the highlight to the newest match",
                followed && counter.Text == "5/5" && pane.PaintedText == "world",
                $"counter '{counter.Text}', painted line {pane.PaintedLine}");

            // A player who has scrolled away keeps the highlight where they left
            // it: the count moves, the view does not.
            pane.Follow = false;
            vm.AppendConsoleLine(LaunchRole.Dedicated, "a new world arrives");
            vm.DrainConsole(LaunchRole.Dedicated);
            for (var i = 0; i < 4; i++)
                win.ConsoleFindTick();
            c("console find: a parked pane only moves its count, not the highlight",
                counter.Text == "5/6" && pane.PaintedLine == 6,
                $"counter '{counter.Text}', painted line {pane.PaintedLine}");

            // --- the case chip re-finds from where the player is ---
            caseToggle.IsChecked = true;
            c("console find: the case chip re-finds from the player's own line",
                counter.Text == "4/5" && vm.FindMatches(LaunchRole.Dedicated).Count == 5,
                $"counter '{counter.Text}' of {vm.FindMatches(LaunchRole.Dedicated).Count} case-sensitive hit(s)");

            input.RaiseEvent(Stroke(Key.Enter));
            c("console find: Enter steps on from the match it is already on",
                counter.Text == "5/5", $"counter '{counter.Text}'");

            closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var cleared = PumpLayout(window, () => pane.PaintedLine < 0, 3000);
            c("console find: closing takes the bar, the count, the query and the highlight away",
                !bar.IsVisible && cleared && string.IsNullOrEmpty(counter.Text)
                && vm.FindQuery(LaunchRole.Dedicated).Length == 0,
                $"visible={bar.IsVisible}, painted line {pane.PaintedLine}, counter '{counter.Text}', query '{vm.FindQuery(LaunchRole.Dedicated)}'");

            // --- routing: the bar belongs to the pane the player is in ---
            // Working in a pane is how that pane becomes the find target, and
            // Ctrl+F then opens *its* bar. The pane hears that as focus landing
            // on one of its own controls — the command line below the output, or
            // the output itself — so the focus a click gives is what this drives:
            // a headless window cannot deliver the press (see the note above
            // Press's old home), but focus reaches exactly the same handler.
            var clientCmd = window.FindControl<TextBox>("TxtConsoleClientCmd");
            clientCmd?.Focus();
            Dispatcher.UIThread.RunJobs();
            var ctrlF = Stroke(Key.F, KeyModifiers.Control);
            window.RaiseEvent(ctrlF);
            Dispatcher.UIThread.RunJobs();
            c("console find: working in a pane aims Ctrl+F at that pane's bar",
                clientBar.IsVisible && !bar.IsVisible && ctrlF.Handled && clientCmd is not null,
                $"client bar={clientBar.IsVisible}, server bar={bar.IsVisible}, ctrl+F handled={ctrlF.Handled}, client command line focused={clientCmd?.IsFocused}");

            // Enter, not the wait: the debounce is a timer, and a timer created
            // outside the suite's job does not tick in this harness (see the note
            // on the debounce check above) — so the query is flushed the way a
            // player finishes one, with the key.
            clientInput.Text = "alpha";
            Dispatcher.UIThread.RunJobs();
            clientInput.RaiseEvent(Stroke(Key.Enter));
            var clientCounted = PumpLayout(window, () => clientCounter.Text == "1/1", 3000);
            var clientPainted = PumpLayout(window, () => clientPane.PaintedLine >= 0, 3000);
            c("console find: the client bar's box searches and paints the client pane",
                clientCounted && clientPainted && clientPane.PaintedLine == 4
                && clientPane.PaintedText == "alpha" && vm.FindQuery(LaunchRole.Dedicated).Length == 0,
                $"counter '{clientCounter.Text}', painted line {clientPane.PaintedLine} '{clientPane.PaintedText}'");
            c("console find: the server pane's highlight is not the client's to move",
                pane.PaintedLine < 0, $"server painted line {pane.PaintedLine}");

            var escape = Stroke(Key.Escape);
            clientInput.RaiseEvent(escape);
            c("console find: Escape in the box closes that pane's bar and forgets its search",
                !clientBar.IsVisible && escape.Handled
                && vm.FindQuery(LaunchRole.Client).Length == 0,
                $"visible={clientBar.IsVisible}, query '{vm.FindQuery(LaunchRole.Client)}'");
        }
        finally
        {
            input.Text = "";
            clientInput.Text = "";
            Dispatcher.UIThread.RunJobs();
            caseToggle.IsChecked = false;
            foreach (var open in new[] { bar, clientBar })
                open.IsVisible = false;
            vm.RebuildFind(LaunchRole.Dedicated, "", false, 0);
            vm.RebuildFind(LaunchRole.Client, "", false, 0);
            pane.ClearFindMatch();
            clientPane.ClearFindMatch();
            pane.Follow = follows;
            clientPane.Follow = clientFollows;
            vm.ClearConsole(LaunchRole.Dedicated);
            vm.ClearConsole(LaunchRole.Client);

            // Hand the server pane back its aim, the way the player returning to
            // that tab does: its command line takes the focus, so the next
            // Ctrl+F would open the server's bar again.
            window.FindControl<TextBox>("TxtConsoleServerCmd")?.Focus();
            Dispatcher.UIThread.RunJobs();
            tabLocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    // The pointer-press route is not exercised here, and cannot be: a headless
    // window has no rendered surface for the renderer's hit test to walk, so
    // InputHitTest over a pane returns the frame *around* the pane (measured:
    // "candidate 549.5, 480.5 -> Border:<Grid:<Grid:RootChrome"), and every
    // press lands outside it no matter what the pane draws — transparent
    // backgrounds on the scroll viewer, the list and a border around the list
    // all failed to change that. What a press does to the find bar is therefore
    // checked through the focus that the same press produces, which is the part
    // the pane actually listens to (MainWindow.axaml.cs, AddConsoleFindBar).
    // The gap is the routing of the press itself, not of the aim it carries.

    /// <summary>A keystroke, as the window's own handlers see one.</summary>
    static KeyEventArgs Stroke(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    /// <summary>A pane with exactly these lines in it, and a revision that knows
    /// it: the queue is drained, because that is what the window does.</summary>
    static void Seed(MainViewModel vm, LaunchRole role, string[] lines)
    {
        vm.ClearConsole(role);
        foreach (var line in lines)
            vm.AppendConsoleLine(role, line);
        vm.DrainConsole(role);
    }

    /// <summary>The line the current match is on, which is the anchor a step
    /// hands back — the same expression the window's bar uses.</summary>
    static int MatchLine(MainViewModel vm, LaunchRole role)
    {
        var i = vm.FindCurrent(role);
        var matches = vm.FindMatches(role);
        return i >= 0 && i < matches.Count ? matches[i].Line : 0;
    }

    /// <summary>
    /// Run the dispatcher until a task settles. The suite is a posted job on the
    /// UI thread, so a continuation the view model posted has nowhere to go while
    /// this thread sits inside a check; pumping is what a real message loop would
    /// be doing meanwhile.
    /// </summary>
    static bool PumpUntil(Task task, int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
        return task.IsCompleted;
    }

    /// <summary>The same, for an outcome a posted handler produces rather than a
    /// task.</summary>
    static bool PumpWaitFor(Func<bool> until, int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (!until() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
        return until();
    }

    /// <summary>
    /// STOP, and the two other ways the port was not stopping anything.
    ///
    /// <para>What the player reported: the console tab's Stop did not force the
    /// game and the dedicated server closed, and Clear did nothing for both logs.
    /// Both were bindings — Stop was the dedi's own kill
    /// (<c>KillDediCommand</c>: one image, no client, the session left standing) and
    /// Clear was the dedi's pane alone — so what this group pins is the
    /// <em>request</em> each button now makes, together with the rules the kill
    /// itself is built from.</para>
    ///
    /// <para><b>Nothing here kills anything.</b> The kill goes through
    /// <see cref="MainViewModel.KillOverride"/>, so the suite reads back which roles
    /// were asked for, under which root, with which of this launcher's own children
    /// — and the sweep's own rules are checked as pure functions
    /// (<c>GameProcesses</c>). The one process the group does involve is a
    /// throwaway <c>sleep</c> of its own, which it disposes. A suite that ran the
    /// real sweep would be a suite that kills the player's game, and this suite runs
    /// on the player's machine.</para>
    /// </summary>
    static void StopChecks(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // --- the rules the sweep is made of: pure, so they can be checked ---
        var clientImages = GameProcesses.ImagesFor(LaunchRole.Client);
        c("stop: a client kill names both client images, DX12 included",
            clientImages.Contains("r5apex.exe", StringComparer.Ordinal)
            && clientImages.Contains("r5apex_dx12.exe", StringComparer.Ordinal),
            string.Join(", ", clientImages));

        c("stop: the dedicated role names only the dedi image",
            GameProcesses.ImagesFor(LaunchRole.Dedicated)
                is ["r5apex_ds.exe"],
            string.Join(", ", GameProcesses.ImagesFor(LaunchRole.Dedicated)));

        // A substring search is what <c>pkill -f</c> is, and it is why the old
        // pattern could match a file that merely has the name in it.
        var protonLine = @"C:\windows\system32\wine-preloader Z:\home\vibrantvibez\Games\r5flowstate\r5apex_ds.exe -port 37015";
        c("stop: a Proton command line is matched at the image's leaf",
            GameProcesses.NamesImage(protonLine, "r5apex_ds.exe")
            && GameProcesses.NamesImage("/usr/bin/wine \"Z:/x/r5apex_dx12.exe\"", "r5apex_dx12.exe")
            && GameProcesses.NamesImage("Z:/x/r5apex.exe", "r5apex.exe"));

        c("stop: and a name that merely contains the image is not a match",
            !GameProcesses.NamesImage("/tmp/r5apex_ds.exe.bak", "r5apex_ds.exe")
            && !GameProcesses.NamesImage("/tmp/r5apex_ds.exe.log", "r5apex_ds.exe")
            && !GameProcesses.NamesImage("/tmp/notr5apex.exe", "r5apex.exe")
            && !GameProcesses.NamesImage("", "r5apex.exe"),
            "suffix, prefix and empty all refused");

        var root = "/home/player/Games/r5flowstate";
        c("stop: the install root is the scope, spelled as a Unix path or as Wine's Z:",
            GameProcesses.IsUnderRoot("wine Z:\\home\\player\\Games\\r5flowstate\\r5apex.exe",
                null, root)
            && GameProcesses.IsUnderRoot("wine /home/player/Games/r5flowstate/r5apex.exe",
                null, root)
            && GameProcesses.IsUnderRoot("wine r5apex.exe", root, root),
            "the argument, the Z: mapping, and the working directory");

        c("stop: another install root's game is not this one's",
            !GameProcesses.IsUnderRoot("wine /mnt/games/r5flowstate-other/r5apex.exe", null, root)
            && !GameProcesses.IsUnderRoot("wine /mnt/games/r5flowstate-other/r5apex.exe",
                "/mnt/games/r5flowstate-other", root),
            "a sibling path that shares a prefix is still a different install");

        c("stop: a blank root is no scope at all, so the sweep finds nothing",
            !GameProcesses.IsUnderRoot("wine /home/player/Games/r5flowstate/r5apex.exe", null, "")
            && GameProcesses.KillRoles(new[] { LaunchRole.Client }, "", null) == 0,
            "upstream's rule: no root, no unscoped sweep");

        // The scan itself, read-only, with an image name nothing on any machine can
        // carry — so this proves the sweep runs without asking it to find a game.
        var nothing = GameProcesses.Candidates(new[] { "r5f-no-such-image.exe" }, root);
        c("stop: the sweep reads /proc, and finds nothing for an image nothing carries",
            nothing.Count == 0,
            nothing.Count == 0 ? "one pass over /proc, no candidates"
                : $"{nothing.Count} candidate(s) for an image that cannot exist");

        // --- what each button asks for ---
        var requests = new ConcurrentQueue<(string? Root, string Roles, int Tracked)>();
        var realKill = MainViewModel.KillOverride;
        MainViewModel.KillOverride = (killRoot, roles, tracked) =>
        {
            requests.Enqueue((killRoot, string.Join("+", roles.OrderBy(r => r.ToString())),
                tracked.Count(p => p is not null)));
            return 0;
        };

        Process? sleep = null;
        try
        {
            // A session that looks like a live one: an armed match, a steering
            // session, and a console of this launcher's own (the tracked child).
            vm.DisarmHostedMatch();
            vm.DropLocalRcon();
            sleep = Spawn("sleep 30", stdin: false);
            vm.AttachConsole(LaunchRole.Dedicated, ConsoleTap.Attach(LaunchRole.Dedicated, sleep));
            vm.OpenRcon(vm.Port);
            vm.ArmHostedMatch();
            var live = PumpWaitFor(() => vm.ServerConsoleLive, 4000);

            var beforeStop = vm.SimpleStatus;
            vm.StopSessionCommand.Execute(null);
            var settled = PumpWaitFor(() => !requests.IsEmpty, 6000);
            var stop = requests.TryDequeue(out var first) ? first : default;
            c("stop: the session stop asks for both roles, under the install root",
                settled && live && stop.Roles is "Client+Dedicated"
                && string.Equals(stop.Root, vm.InstallPath, StringComparison.Ordinal),
                $"roles='{stop.Roles}', root='{stop.Root}', install='{vm.InstallPath}'");

            c("stop: and the children this launcher started go with it",
                settled && stop.Tracked == 1,
                $"{stop.Tracked} tracked child(ren) handed to the kill");

            c("stop: STOP leaves no session standing",
                settled && !vm.HostedMatchArmed && !vm.HostSteeringReady
                && !vm.ServerConsoleLive && !vm.ClientConsoleLive
                && string.Equals(vm.SimpleStatus, Loc.Get("status_stopped"), StringComparison.Ordinal),
                $"armed={vm.HostedMatchArmed}, steering={vm.HostSteeringReady}, "
                + $"server={vm.ServerConsoleLive}, client={vm.ClientConsoleLive}, "
                + $"status '{beforeStop}' -> '{vm.SimpleStatus}'");

            // The two role kills stay role-scoped: a client kill that took the dedi
            // with it would be the same bug in the other direction.
            vm.KillClientCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var client = requests.TryDequeue(out var second) ? second : default;
            c("stop: the client's own kill asks for the client role",
                client.Roles is "Client" && string.Equals(client.Root, vm.InstallPath, StringComparison.Ordinal),
                $"roles='{client.Roles}'");

            vm.KillDediCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var dedi = requests.TryDequeue(out var third) ? third : default;
            c("stop: the dedi's own kill asks for the dedicated role",
                dedi.Roles is "Dedicated" && string.Equals(dedi.Root, vm.InstallPath, StringComparison.Ordinal),
                $"roles='{dedi.Roles}'");
        }
        finally
        {
            MainViewModel.KillOverride = realKill;
            try { sleep?.Kill(entireProcessTree: true); } catch { /* already gone */ }
            sleep?.Dispose();
            vm.DisarmHostedMatch();
            vm.DropLocalRcon();
            vm.DropConsoles();
        }

        // --- Clear, both logs ---
        foreach (var role in new[] { LaunchRole.Dedicated, LaunchRole.Client })
        {
            vm.AppendConsoleLine(role, $"a line in the {role} pane");
            vm.DrainConsole(role);
        }

        var seeded = vm.Lines(LaunchRole.Dedicated).Count > 0 && vm.Lines(LaunchRole.Client).Count > 0;
        c("stop: both panes have something to clear", seeded,
            $"server={vm.Lines(LaunchRole.Dedicated).Count}, client={vm.Lines(LaunchRole.Client).Count}");

        // The dedi's own command first, so the pair is shown to be a different
        // answer rather than the same one twice.
        vm.ClearConsoleServerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        c("stop: the dedi's own clear is the dedi's pane alone",
            vm.Lines(LaunchRole.Dedicated).Count == 0 && vm.Lines(LaunchRole.Client).Count > 0,
            $"server={vm.Lines(LaunchRole.Dedicated).Count}, client={vm.Lines(LaunchRole.Client).Count}");

        vm.ClearConsoleBothCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        c("clear: one button clears both logs, and the queues with them",
            vm.Lines(LaunchRole.Dedicated).Count == 0 && vm.Lines(LaunchRole.Client).Count == 0
            && !vm.HasPendingConsole,
            $"server={vm.Lines(LaunchRole.Dedicated).Count}, client={vm.Lines(LaunchRole.Client).Count}, "
            + $"pending={vm.HasPendingConsole}");
    }

    // ------------------------------------------------------------ plumbing

    /// <summary>
    /// The layout pump, for anything that waits on a realized control: a
    /// virtualized line has nothing to select until the list has laid it out,
    /// and a headless window only lays out when it is pumped — a real one is
    /// doing it every frame.
    /// </summary>
    static bool PumpLayout(Window window, Func<bool> until, int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (until())
                return true;
            Thread.Sleep(20);
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return until();
    }

    /// <summary>A throwaway child that prints whatever the script says.</summary>
    static Process Spawn(string script, bool stdin, bool capture = true)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = stdin,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        return Process.Start(psi)!;
    }

    /// <summary>Poll until the condition holds or the budget runs out. The tap
    /// delivers on its own threads, so every check waits on an outcome rather
    /// than on a sleep.</summary>
    static bool WaitFor(Func<bool> until, int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (until())
                return true;
            Thread.Sleep(20);
        }
        return until();
    }
}
