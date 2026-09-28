using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>One coloured piece of a console line. Windows built a WPF
/// <c>Run</c> here; a span of text plus a brush is the same information without
/// the visual, which is what lets the ANSI reader be checked offline.</summary>
public sealed class ConsoleSpan
{
    public ConsoleSpan(string text, IBrush? brush)
    {
        Text = text;
        Brush = brush;
    }

    public string Text { get; }

    /// <summary>Null means "the pane's own foreground" — the state a line starts
    /// in and returns to at every SGR 0.</summary>
    public IBrush? Brush { get; }
}

/// <summary>One console line, already split into coloured spans.</summary>
public sealed class ConsoleLineVM
{
    public ConsoleLineVM(string text, IReadOnlyList<ConsoleSpan> spans)
    {
        Text = text;
        Spans = spans;
    }

    /// <summary>The line as plain text (escapes removed) — what find searches.</summary>
    public string Text { get; }

    public IReadOnlyList<ConsoleSpan> Spans { get; }

    /// <summary>An empty line still occupies a row: console output has blank
    /// lines in it and dropping them would misreport the boot.</summary>
    public bool HasSpans => Spans.Count > 0;
}

/// <summary>One match inside one line of a pane. Windows holds a WPF
/// <c>TextRange</c> here; a line index plus an offset is the same information
/// without the document, and it is what the pane can actually paint (see
/// <c>ConsolePane.ShowFindMatch</c>).</summary>
public readonly record struct ConsoleFindMatch(int Line, int Start, int Length);

/// <summary>
/// One pane's find state — Windows' <c>ConsoleFindState</c> without the view
/// references: the query, whether it is case-sensitive, the matches and where
/// the player is in them.
/// </summary>
sealed class ConsoleFindState
{
    public string Query = "";
    public bool MatchCase;
    public readonly List<ConsoleFindMatch> Matches = new();
    public int Current = -1;
    public bool Capped;
    public int Rev = -1;
}

/// <summary>What a rebuild or a step produced, for the window to paint: the
/// matches, where the player is, and the counter line.</summary>
public readonly record struct ConsoleFindView(
    int Count, int Current, bool Capped, string Counter, ConsoleFindMatch? Match)
{
    /// <summary>True when there is something to paint.</summary>
    public bool HasMatch => Match is not null;
}

/// <summary>
/// Console tab — port of the Windows launcher's MainWindow.Console.cs (the two
/// panes, the batching, the trim, the command line and the classifiers that run
/// over the dedi's output) onto the ported <see cref="ConsoleTap"/>.
///
/// The split is the same one the leaderboards and blog tabs use: everything that
/// is state or logic lives here, and the window keeps what needs a Dispatcher —
/// the 250 ms drain timer, the scroll-to-end when a pane is following, the
/// key handling on the two command boxes, and the find bar. Windows keeps the
/// queues and the documents in its code-behind; moving the queues here is what
/// makes the batching, the trim and the ANSI reader reachable from the offline
/// suite.
///
/// One deliberate divergence. Windows renders each pane as a single
/// <c>RichTextBox</c> document (one <c>Paragraph</c> of <c>Run</c>s, trimmed by
/// dropping inlines up to a <c>LineBreak</c>). Avalonia has no <c>FlowDocument</c>,
/// and one <c>TextBlock</c> holding four thousand lines of runs would rebuild its
/// whole <c>TextLayout</c> on every arriving batch — the exact cost Windows'
/// batching exists to avoid. So a pane is a virtualized list of lines: the same
/// text, the same colours, the same trim (a thousand lines at a time), and
/// selection and find per line instead of across the document.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Boot alone is tens of thousands of lines. Appending each one as
    /// it arrives relayouts the pane per line and locks the UI thread, so lines
    /// queue and land in timed batches instead — Windows' own numbers.</summary>
    public const int ConsoleMaxLines = 4000;
    public const int ConsoleTrimLines = 1000;
    public const int ConsoleBatchLimit = 2000;

    /// <summary>Command history depth, oldest dropped first.</summary>
    public const int ConsoleHistoryCap = 50;

    /// <summary>How long a local play waits for the dedi to report a live level
    /// before starting the client anyway (Windows: HostReadySpawnAnywaySeconds).
    /// The client then joins a server that is not up yet, which is why it is an
    /// escape hatch and not the plan.</summary>
    public const int HostReadySpawnAnywaySeconds = 75;

    readonly ConcurrentQueue<string> _pendingServer = new();
    readonly ConcurrentQueue<string> _pendingClient = new();

    ConsoleTap? _tapServer;
    ConsoleTap? _tapClient;
    HostReadyGate? _hostGate;

    string _lastScriptError = "";
    DateTime _lastScriptErrorUtc = DateTime.MinValue;

    /// <summary>The readiness wait that is in flight, if any. It is held here so
    /// <see cref="DropConsoles"/> can end it: a player who kills the server
    /// while the launcher is waiting for it is not going to get a live level,
    /// and the wait must not keep the launch lane busy until the 75-second
    /// escape hatch fires.</summary>
    CancellationTokenSource? _hostWaitCts;

    /// <summary>Window of time after an uncaught script error in which a host
    /// teardown counts as that error ending the match, rather than the teardown
    /// every boot and changelevel prints.</summary>
    // Windows' own value (MainWindow.xaml.cs:82). It used to say 30 s here,
    // which is the port's invention: Windows reads a teardown as "the script
    // error ending the match" only within 20 s of the error, so 30 s would call
    // a teardown fatal ten seconds after Windows stops believing it.
    static readonly TimeSpan ScriptErrorShutdownWindow = TimeSpan.FromSeconds(20);

    [ObservableProperty] private ObservableCollection<ConsoleLineVM> _consoleServerLines = new();
    [ObservableProperty] private ObservableCollection<ConsoleLineVM> _consoleClientLines = new();

    /// <summary>True while the launcher is waiting for a dedi to report a live
    /// level — the Play button and the console's own action button read it.</summary>
    [ObservableProperty] private bool _consoleWaitingForHost;

    /// <summary>A line was queued, from a reader thread. The window starts its
    /// drain timer; the queues are the truth, so a missed raise only delays a
    /// batch by one tick.</summary>
    public event Action? ConsoleLineQueued;

    /// <summary>A pane was reset (cleared, or its console replaced) — the window
    /// drops its follow flag and its find state for that pane.</summary>
    public event Action<LaunchRole>? ConsoleReset;

    /// <summary>Something the player should see in the pane's own voice — a
    /// command that could not be delivered, a server that died.</summary>
    public event Action<LaunchRole, string>? ConsoleNoted;

    /// <summary>A role's console was taken over, which means a launch just
    /// happened. The window hangs "open the Console tab on launch" off this —
    /// Windows calls MaybeOpenConsoleForLaunch after a launch succeeds, and
    /// attaching a tap is what a successful launch looks like from here.</summary>
    public event Action<LaunchRole>? ConsoleAttached;

    public bool ServerConsoleLive => _tapServer is not null;
    public bool ClientConsoleLive => _tapClient is not null;

    internal ConsoleTap? TapFor(LaunchRole role) => role == LaunchRole.Dedicated ? _tapServer : _tapClient;

    /// <summary>
    /// Touch a bound property, a bound collection, or a view from wherever the
    /// caller happens to be. The console has two sources that are not the UI
    /// thread — the tap's reader threads and PLAY's launch task — and an
    /// <c>ObservableObject</c>, like Avalonia itself, does not marshal for us.
    ///
    /// <see cref="Invoke"/> is the synchronous form and is what the ordered
    /// paths use: attaching a console has to be finished before the caller can
    /// hand the same role to <see cref="DetachConsole"/>, and an echo has to
    /// land before the note that follows it.
    /// </summary>
    static void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    // The fire-and-forget form already exists — the notes fetch needs it too —
    // so the hot paths here use that one (MainViewModel.Notes): a reader thread
    // must not block on the UI thread once per line.

    // ------------------------------------------------------------ lifecycle

    /// <summary>
    /// Take over a role's console, and start its tap. Ownership is what starts
    /// the reading: a tap reads the child's streams once, at the moment it
    /// reads them, so it has to have somewhere to deliver them before it starts
    /// — otherwise the first lines of a boot, which are the ones that explain a
    /// launch that went wrong, are delivered to nobody.
    /// </summary>
    public void AttachConsole(LaunchRole role, ConsoleTap tap)
    {
        // On the UI thread as one step. The clear has to happen before the tap
        // is wired (otherwise the previous run's leftovers land in this run's
        // pane) and the wiring has to be done before the caller can detach — so
        // this is the synchronous form, not a post.
        Invoke(() =>
        {
            DetachConsole(role);
            ClearConsole(role);

            if (role == LaunchRole.Dedicated)
            {
                _tapServer = tap;
                tap.LineReceived += line =>
                {
                    AppendConsoleLine(LaunchRole.Dedicated, line);
                    NoteDediLine(line);
                };
                AppendConsoleLine(LaunchRole.Dedicated, "(server console)");
            }
            else
            {
                _tapClient = tap;
                tap.LineReceived += line =>
                {
                    AppendConsoleLine(LaunchRole.Client, line);
                    NoteClientLine(line);
                };
                AppendConsoleLine(LaunchRole.Client, "(client console)");
            }

            OnPropertyChanged(nameof(ServerConsoleLive));
            OnPropertyChanged(nameof(ClientConsoleLive));
            OnPropertyChanged(nameof(ConsoleServerPaneOpen));

            // Inside the same hop: the window's handler brings a tab forward,
            // which is a view edit.
            ConsoleAttached?.Invoke(role);

            // Wired, so it may read. A caller that already started the tap
            // (the suite does, to check the tap on its own) is not affected:
            // Start is idempotent.
            tap.Start();
        });
    }

    /// <summary>Drop a role's console without clearing its pane: what the pane
    /// shows is the record of the run that just ended, and the player may still
    /// be reading it.</summary>
    public void DetachConsole(LaunchRole role)
    {
        Invoke(() =>
        {
            if (role == LaunchRole.Dedicated)
            {
                _tapServer?.Dispose();
                _tapServer = null;
            }
            else
            {
                _tapClient?.Dispose();
                _tapClient = null;
            }

            OnPropertyChanged(nameof(ServerConsoleLive));
            OnPropertyChanged(nameof(ClientConsoleLive));
            OnPropertyChanged(nameof(ConsoleServerPaneOpen));
        });
    }

    /// <summary>Close both consoles and any readiness wait — the launcher is
    /// shutting down, or the game tree has been killed.</summary>
    public void DropConsoles()
    {
        _hostWaitCts?.Cancel();
        DetachConsole(LaunchRole.Dedicated);
        DetachConsole(LaunchRole.Client);
    }

    // -------------------------------------------------------------- batches

    /// <summary>Queued, not written: the window's timer owns every pane edit, so
    /// a reader thread never touches a bound collection.</summary>
    public void AppendConsoleLine(LaunchRole role, string line)
    {
        Queue(role).Enqueue(line);

        // The nudge goes across to the UI thread once per batch, not once per
        // line: a boot prints tens of thousands of lines, and the timer only
        // needs to know that the queue is no longer empty. DrainConsole clears
        // the flag, and the window's tick restarts the timer itself if a line
        // slips in between the two — so a nudge can never be lost.
        if (Interlocked.Exchange(ref _consoleWakePending, 1) == 0)
            Post(() => ConsoleLineQueued?.Invoke());
    }

    int _consoleWakePending;

    ConcurrentQueue<string> Queue(LaunchRole role) =>
        role == LaunchRole.Dedicated ? _pendingServer : _pendingClient;

    public bool HasPendingConsole =>
        !_pendingServer.IsEmpty || !_pendingClient.IsEmpty;

    /// <summary>Move up to <see cref="ConsoleBatchLimit"/> queued lines into a
    /// pane and trim it. Returns how many lines landed, so the window only
    /// scrolls and repaints when something changed.</summary>
    public int DrainConsole(LaunchRole role)
    {
        var queue = Queue(role);
        if (queue.IsEmpty)
            return 0;

        var taken = 0;
        while (taken < ConsoleBatchLimit && queue.TryDequeue(out var line))
        {
            Lines(role).Add(ParseAnsiLine(line));
            taken++;
        }

        Interlocked.Exchange(ref _consoleWakePending, 0);
        TrimConsole(role);
        BumpRevision(role);
        return taken;
    }

    /// <summary>The pane's lines. Internal because the suite reads them back to
    /// check the ANSI reader and the trim without rendering a frame.</summary>
    internal ObservableCollection<ConsoleLineVM> Lines(LaunchRole role) =>
        role == LaunchRole.Dedicated ? ConsoleServerLines : ConsoleClientLines;

    /// <summary>Keep at most <see cref="ConsoleMaxLines"/>, dropping the oldest
    /// <see cref="ConsoleTrimLines"/> in one go: trimming per line would churn
    /// the list four thousand times over a boot.</summary>
    void TrimConsole(LaunchRole role)
    {
        var lines = Lines(role);
        if (lines.Count <= ConsoleMaxLines)
            return;

        var drop = lines.Count - (ConsoleMaxLines - ConsoleTrimLines);
        for (var i = 0; i < drop; i++)
            lines.RemoveAt(0);
    }

    public void ClearConsole(LaunchRole role)
    {
        var queue = Queue(role);
        while (queue.TryDequeue(out _))
        {
        }

        Lines(role).Clear();
        ResetFind(role);
        Invoke(() => ConsoleReset?.Invoke(role));
    }

    [RelayCommand]
    private void ClearConsoleServer() => ClearConsole(LaunchRole.Dedicated);

    [RelayCommand]
    private void ClearConsoleClient() => ClearConsole(LaunchRole.Client);

    /// <summary>
    /// Windows' OnConsoleClear: one button, and both panes.
    ///
    /// <para>The two logs are one session's — the dedi the launcher started and the
    /// client that joined it — so clearing one and leaving the other is a half-done
    /// clear, and the tooltip has said "clear both logs" since the port was
    /// written. The button was bound to the dedi's own command
    /// (<see cref="ClearConsoleServer"/>), which is why the player reported Clear
    /// as not working for both logs. The two single-role commands stay: they are
    /// what the suite drives, and a pane on its own is still a thing a future
    /// button could want.</para>
    /// </summary>
    [RelayCommand]
    private void ClearConsoleBoth()
    {
        ClearConsole(LaunchRole.Dedicated);
        ClearConsole(LaunchRole.Client);
    }

    // ---------------------------------------------------------------- ANSI

    static readonly ConcurrentDictionary<int, IBrush> s_ansiBrushes = new();

    /// <summary>
    /// One line as spans. Windows' scanner, which knows what the SDK emits:
    /// truecolor only, <c>38;2;r;g;b</c> to set and <c>0</c> to reset, and any
    /// other escape (cursor moves, erase-to-EOL) leaves the current colour
    /// alone. The escape sequences themselves never reach the pane.
    ///
    /// Internal so the suite can check the colouring without a window.</summary>
    internal static ConsoleLineVM ParseAnsiLine(string line)
    {
        var spans = new List<ConsoleSpan>();
        IBrush? colour = null;
        var i = 0;
        var run = 0;
        while (i < line.Length)
        {
            if (line[i] != '\u001b')
            {
                i++;
                continue;
            }

            if (i > run)
                spans.Add(new ConsoleSpan(line[run..i], colour));

            var end = i + 1;
            if (end < line.Length && line[end] == '[')
            {
                end++;
                while (end < line.Length && !char.IsLetter(line[end]))
                    end++;
                if (end < line.Length && line[end] == 'm')
                    colour = ApplySgr(line[(i + 2)..end], colour);
                if (end < line.Length)
                    end++;
            }
            else if (end < line.Length)
            {
                end++;
            }

            i = end;
            run = end;
        }

        if (run < line.Length)
            spans.Add(new ConsoleSpan(line[run..], colour));

        return new ConsoleLineVM(ConsoleTap.StripAnsi(line), spans);
    }

    /// <summary>The palette is the SDK's own, so the brushes are cached by rgb:
    /// a boot repeats a handful of colours across thousands of lines.</summary>
    static IBrush? ApplySgr(string body, IBrush? current)
    {
        if (body.Length == 0 || body == "0")
            return null;

        var parts = body.Split(';');
        if (parts.Length >= 5 && parts[0] == "38" && parts[1] == "2"
            && int.TryParse(parts[2], out var r)
            && int.TryParse(parts[3], out var g)
            && int.TryParse(parts[4], out var b)
            && r is >= 0 and <= 255 && g is >= 0 and <= 255 && b is >= 0 and <= 255)
        {
            var key = (r << 16) | (g << 8) | b;
            return s_ansiBrushes.GetOrAdd(key, _ =>
            {
                var brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
                brush.ToImmutable();
                return brush;
            });
        }

        return current;
    }

    // --------------------------------------------------------- the dedi's own

    /// <summary>
    /// Classify a line of dedi output. The same reading Windows' OnHostedDediLine
    /// does: a ready line satisfies the wait, a changelevel's ready line settles
    /// a pending map change, a script error is remembered (because host teardown
    /// prints on every boot), and only a fatal classification ends the run.
    /// </summary>
    void NoteDediLine(string line)
    {
        if (HostReadyGate.IsReadyLine(line, LiveMap))
        {
            _hostGate?.SignalFromConsole();
            // The host is up, so the heartbeat's own grace starts here (Windows
            // signals the event and calls NoteHostLevelReady on the same line).
            NoteHostLevelReady();
        }

        // A changelevel is done when the dedi says the map *it was asked for* is
        // ready, which is a different line from a boot's readiness (Windows'
        // OnHostedDediLine, MainWindow.Console.cs:800).
        var pending = _pendingChangeMap;
        if (pending is not null && HostReadyGate.IsMapReadyLine(line, pending))
            NoteMapChangeReady();

        if (HostReadyGate.IsLevelLoadLine(line))
            NoteHostLevelLoad();

        var scriptError = HostReadyGate.ScriptErrorExcerpt(line);
        if (scriptError is not null)
        {
            _lastScriptError = scriptError;
            _lastScriptErrorUtc = DateTime.UtcNow;
            return;
        }

        var fatal = HostReadyGate.IsFatalLine(line);
        if (!fatal && HostReadyGate.IsHostShutdownLine(line))
        {
            // Teardown right after an uncaught script error is the error ending
            // the match; every other teardown is boot or changelevel. Windows
            // adds one more clause to that (:823): the host must have been ready
            // at least once, because a boot that never got there is a launch
            // that failed, not a match that ended -- a teardown after a boot
            // error is the boot unwinding, and the readiness wait has its own
            // exits for it. The heartbeat's grace field is where "the host has
            // been ready" is written down (MaxValue means it never has).
            fatal = _lastScriptError.Length > 0
                && DateTime.UtcNow - _lastScriptErrorUtc < ScriptErrorShutdownWindow
                && _hostHeartbeatGraceUntilUtc != DateTime.MaxValue;
            if (fatal)
                NoteHostLevelLoad();
        }

        if (!fatal)
            return;

        _hostGate?.SignalFatal();

        // The fault lane reads the excerpt at this point, before anything else
        // can touch it -- Windows reads it into _pendingCrashExcerpt here and
        // puts it in the warning's body, so the player is told which script
        // error killed the match rather than only that it died.
        QueueHostedServerFault(HostedServerFault.Crashed);
    }

    /// <summary>
    /// A level load started: the frame loop stalls here legitimately, so the
    /// "server is not answering" reading is suspended — Windows pushes its
    /// heartbeat grace out to ninety seconds and resets the miss counter here,
    /// and the counter reset is what makes a load that starts after a couple of
    /// misses not count them against the new level. It deliberately does *not*
    /// forget the last script error, which is read straight after this on the
    /// fault path.
    /// </summary>
    void NoteHostLevelLoad()
    {
        if (_hostHeartbeatGraceUntilUtc != DateTime.MaxValue)
            _hostHeartbeatGraceUntilUtc = DateTime.UtcNow + HostHeartbeatGrace;
        _hostHeartbeatMisses = 0;
    }

    /// <summary>
    /// The host reported a live level: the frame loop is running again, so the
    /// grace drops to Windows' ten seconds (the long one belongs to a load in
    /// progress) and the miss counter starts over. Before the first ready line
    /// there is no grace to move — the field is still <c>MaxValue</c>, which is
    /// how "this host has never been ready" is written down.
    /// </summary>
    void NoteHostLevelReady()
    {
        if (_hostHeartbeatGraceUntilUtc != DateTime.MaxValue)
            _hostHeartbeatGraceUntilUtc = DateTime.UtcNow + HostLevelReadyGrace;
        _hostHeartbeatMisses = 0;
    }

    /// <summary>
    /// The last uncaught script error, if it is recent enough to explain what
    /// just happened — Windows' own two-minute window
    /// (<c>RecentScriptError</c>). Null when there is nothing to name, so the
    /// caller picks its own wording instead of formatting a null into a
    /// sentence.
    /// </summary>
    internal string? RecentScriptError() =>
        _lastScriptError.Length > 0
        && DateTime.UtcNow - _lastScriptErrorUtc < TimeSpan.FromMinutes(2)
            ? _lastScriptError
            : null;

    /// <summary>The client's own disconnect dialog: the launcher says what the
    /// game said instead of leaving a client that quietly has no server.
    ///
    /// The same line is also read for platform identity — the game is the only
    /// thing that can say whether EA vouched for this account (see
    /// <see cref="PlatformIdentityLines"/>), and these lines arrive while the
    /// player is watching the console, which is exactly when the verdict is worth
    /// having. Absence of identity lines is never a failure: a client that prints
    /// none of them changes nothing here.</summary>
    void NoteClientLine(string line)
    {
        NotePlatformIdentity(line);

        var text = HostReadyGate.ClientErrorDialogText(line);
        if (text is null)
            return;
        Invoke(() => ConsoleNoted?.Invoke(LaunchRole.Client, text));
        AppendLogThreadSafe("Client: " + text);
    }

    /// <summary>
    /// One client line, classified. The card's identity line follows the last thing
    /// the game said, and a failure is said out loud in the log with the fix,
    /// because the fix is a setting.
    /// </summary>
    void NotePlatformIdentity(string line)
    {
        var verdict = PlatformIdentityLines.Classify(line);
        if (verdict == PlatformIdentityVerdict.None)
            return;

        // The client's own pass repeats itself; keep the newest verdict, but never
        // let a later "waiting" overwrite proof that identity already worked.
        if (PlatformIdentityLines.IsSuccess(_lastIdentityVerdict) && !PlatformIdentityLines.IsSuccess(verdict))
            return;

        var text = PlatformIdentityLines.Describe(verdict);
        var changed = verdict != _lastIdentityVerdict;
        _lastIdentityVerdict = verdict;
        Invoke(() => EaIdentityStatus = text);

        if (changed && PlatformIdentityLines.IsFailure(verdict))
            AppendLogThreadSafe("Client: " + text);
    }

    PlatformIdentityVerdict _lastIdentityVerdict = PlatformIdentityVerdict.None;

    // ----------------------------------------------------------- readiness

    /// <summary>
    /// Wait for the dedi to report a live level, with Windows' escape hatch: if
    /// the tagged line has not arrived by
    /// <see cref="HostReadySpawnAnywaySeconds"/>, the caller is told to start the
    /// client anyway rather than keeping the player waiting forever. Returns
    /// whether the host reported ready (false means "started anyway").
    /// </summary>
    public async Task<bool> WaitHostReadyAsync(Process? dedi, CancellationToken ct)
    {
        var gate = _hostGate;
        if (gate is null)
            return false;

        // The caller's token says "this launch is over"; the launcher's own says
        // "the game tree is gone". The wait has to end on either, so it runs on
        // a linked source — which is also what DropConsoles cancels.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _hostWaitCts = linked;

        try
        {
            var started = DateTime.UtcNow;
            var waitTask = Task.Run(() => gate.Wait(dedi, linked.Token), CancellationToken.None);
            var timedOut = false;

            while (!waitTask.IsCompleted)
            {
                var sec = Math.Max(0, (int)(DateTime.UtcNow - started).TotalSeconds);
                if (sec >= HostReadySpawnAnywaySeconds)
                {
                    timedOut = true;
                    break;
                }

                var elapsed = sec;
                Invoke(() =>
                {
                    ConsoleWaitingForHost = true;
                    SimpleStatus = Loc.Format("status_waiting_server", elapsed);
                });
                var tick = await Task.WhenAny(waitTask, Task.Delay(1000, linked.Token)).ConfigureAwait(true);
                if (ReferenceEquals(tick, waitTask))
                    break;

                // A cancelled token completes Task.Delay at once, so without
                // this the loop would spin for the gate's poll interval. The
                // launch is over either way: report it as "not ready".
                if (linked.IsCancellationRequested)
                {
                    AppendLogThreadSafe("Host wait cancelled: the launch ended before the server came up.");
                    return false;
                }
            }

            if (timedOut)
            {
                AppendLogThreadSafe($"Host did not report a live level within {HostReadySpawnAnywaySeconds}s; starting anyway.");
                return false;
            }

            var wait = await waitTask.ConfigureAwait(true);
            if (wait == HostReadyWait.Ready)
            {
                // The host is up: from here the heartbeat may ask, and the
                // fifteen seconds of grace are Windows' own (:2609-2610). Until
                // this point the grace field is MaxValue, which is what "the
                // host has never reported a live level" means to the watchdog
                // and to the teardown rule in NoteDediLine.
                _hostHeartbeatGraceUntilUtc = DateTime.UtcNow + HostReadyGrace;
                _hostHeartbeatMisses = 0;
            }
            else
            {
                AppendLogThreadSafe($"Host wait ended: {wait}.");
            }
            return wait == HostReadyWait.Ready;
        }
        catch (OperationCanceledException)
        {
            AppendLogThreadSafe("Host wait cancelled: the launch ended before the server came up.");
            return false;
        }
        finally
        {
            Invoke(() => ConsoleWaitingForHost = false);
            if (ReferenceEquals(_hostWaitCts, linked))
                _hostWaitCts = null;
        }
    }

    /// <summary>
    /// True when the last launch joined a server of someone else's rather than
    /// starting one of ours — Windows' <c>_joinedServer is not null</c>, minus
    /// the listing itself, because this launcher only needs the yes/no. It is
    /// set by the browser's join and cleared by a launch of our own or by a
    /// disconnect.
    /// </summary>
    [ObservableProperty] private bool _joinedRemote;

    partial void OnJoinedRemoteChanged(bool value) =>
        OnPropertyChanged(nameof(ConsoleServerPaneOpen));

    /// <summary>
    /// Windows' ConsoleServerPaneOpen(): a browser join runs no dedi of ours, so
    /// the server pane would sit there empty with a command box that reaches
    /// nothing. A dedi of ours wins over the flag — the two can both be true,
    /// and it is the dedi that makes the pane useful.
    /// </summary>
    public bool ConsoleServerPaneOpen => !JoinedRemote || ServerConsoleLive;

    /// <summary>Open the rendezvous for a local play. Windows creates a named
    /// event and hands its name to the dedi; here the gate is fed by the dedi's
    /// own console lines (see <see cref="HostReadyGate"/>), so all this does is
    /// replace the previous one — and arm the match. Windows arms after the
    /// dedi's spawn succeeds (ArmHostedMatch, :2552); this port arms here
    /// because the gate is opened immediately before that spawn, and the launch
    /// paths disarm again through DropLocalRcon if it does not happen.</summary>
    internal HostReadyGate? OpenHostGate()
    {
        _hostGate?.Dispose();
        _hostGate = new HostReadyGate();
        // A new match is armed, so the previous match's error explains nothing
        // about this one (Windows clears the same pair in ArmHostedMatch). The
        // match itself is armed by the launch path, once the dedi exists.
        _lastScriptError = "";
        _lastScriptErrorUtc = DateTime.MinValue;
        return _hostGate;
    }

    /// <summary>Signals the gate from outside the console — the suite drives the
    /// wait this way, and so does any future caller that has its own evidence
    /// the level is live.</summary>
    internal void SignalHostReady() => _hostGate?.SignalFromConsole();

    // ---------------------------------------------------------- command line

    /// <summary>Enter in the server pane. The command goes to the dedi's own
    /// stdin through the tap — the same channel the Windows launcher used for a
    /// command box, with RCON reserved for steering a mode/map (see
    /// bridge_setmode), which is what a console command cannot do.</summary>
    public void SubmitServerCommand(string command) => SubmitConsoleCommand(LaunchRole.Dedicated, command);

    public void SubmitClientCommand(string command) => SubmitConsoleCommand(LaunchRole.Client, command);

    internal void SubmitConsoleCommand(LaunchRole role, string command)
    {
        var line = (command ?? "").Trim();
        if (line.Length == 0)
            return;

        var tap = TapFor(role);
        if (tap is null)
        {
            Note(role, Loc.Get("console_not_connected"));
            return;
        }

        if (tap.Role != role)
        {
            Note(role, Loc.Get("console_not_delivered"));
            return;
        }

        if (!tap.TryWriteCommand(line))
        {
            Note(role, Loc.Get("console_not_delivered"));
            return;
        }

        AppendConsoleLine(role, "] " + line);
        RecordConsoleHistory(role, line);
    }

    /// <summary>An echo in the pane's own voice, queued like any other line so
    /// the pane stays single-writer.</summary>
    void Note(LaunchRole role, string text)
    {
        AppendConsoleLine(role, text);
        // AppendConsoleLine has already moved us to the UI thread when it had
        // to; the note follows the echo.
        Post(() => ConsoleNoted?.Invoke(role, text));
    }

    // -------------------------------------------------------------- history

    /// <summary>Oldest first, capped. A repeat of the last command is not stored
    /// twice — the same rule Windows applies on Enter.</summary>
    void RecordConsoleHistory(LaunchRole role, string line)
    {
        var history = HistoryFor(role);
        if (history.Count > 0 && string.Equals(history[^1], line, StringComparison.Ordinal))
            return;

        history.Add(line);
        while (history.Count > ConsoleHistoryCap)
            history.RemoveAt(0);

        if (role == LaunchRole.Dedicated)
            _s.ConsoleHistoryServer = LinuxSettings.FormatHistory(history);
        else
            _s.ConsoleHistoryClient = LinuxSettings.FormatHistory(history);
        try { _s.Save(); }
        catch (Exception ex) { AppendLog("Settings save failed: " + ex.Message); }
    }

    /// <summary>The live history list for a role, seeded from the settings file.
    /// Internal because the window's up/down handling walks it.</summary>
    internal List<string> HistoryFor(LaunchRole role)
    {
        if (role == LaunchRole.Dedicated)
        {
            _historyServer ??= LinuxSettings.ParseHistory(_s.ConsoleHistoryServer);
            return _historyServer;
        }

        _historyClient ??= LinuxSettings.ParseHistory(_s.ConsoleHistoryClient);
        return _historyClient;
    }

    List<string>? _historyServer;
    List<string>? _historyClient;

    // ------------------------------------------------------------ find bar

    /// <summary>Windows' cap: past this the walk stops and the counter says
    /// "10000+" rather than claiming an exact total.</summary>
    public const int ConsoleFindMaxMatches = 10000;

    /// <summary>The anchor that means "the tail of the pane" — a following pane
    /// is looking at the newest lines, so a fresh query should land on the
    /// newest match. Windows has no equivalent: its anchor is always a live
    /// document position, so a missing anchor there wraps to the first match
    /// (see <see cref="IndexAtOrAfter"/>).</summary>
    public const int ConsoleFindTailAnchor = int.MaxValue;

    ConsoleFindState? _findServer;
    ConsoleFindState? _findClient;

    ConsoleFindState FindState(LaunchRole role)
    {
        if (role == LaunchRole.Dedicated)
            return _findServer ??= new ConsoleFindState();
        return _findClient ??= new ConsoleFindState();
    }

    /// <summary>
    /// A pane's revision: it moves whenever the pane's text does. Windows keeps
    /// the same counter per <c>RichTextBox</c> and rebuilds an open bar's matches
    /// when it moves, because a match index means nothing once lines have been
    /// appended or trimmed. A find state starts at -1, so the first rebuild
    /// always happens.
    /// </summary>
    internal int PaneRevision(LaunchRole role) => _revisions[(int)role];

    readonly int[] _revisions = new int[2];

    void BumpRevision(LaunchRole role) => _revisions[(int)role]++;

    /// <summary>The live matches for a role — the suite reads them back.</summary>
    internal IReadOnlyList<ConsoleFindMatch> FindMatches(LaunchRole role) =>
        FindState(role).Matches;

    internal int FindCurrent(LaunchRole role) => FindState(role).Current;

    internal bool FindCapped(LaunchRole role) => FindState(role).Capped;

    /// <summary>The query the last rebuild used, not what is in the box: the box
    /// is debounced (150 ms, Windows' interval), so they differ for that long.</summary>
    internal string FindQuery(LaunchRole role) => FindState(role).Query;

    /// <summary>
    /// A cleared pane is a new pane: the old matches point at lines that no
    /// longer exist, and the paint must go with them.
    /// </summary>
    internal void ResetFind(LaunchRole role)
    {
        var st = FindState(role);
        st.Matches.Clear();
        st.Current = -1;
        st.Capped = false;
        st.Rev = -1;
        BumpRevision(role);
    }

    /// <summary>
    /// Re-find from scratch. The anchor is a line number — the caller passes the
    /// current match's line while the player is stepping through matches, or
    /// <see cref="ConsoleFindTailAnchor"/> while the pane is following the tail —
    /// so a rebuild caused by arriving output keeps the player where they were.
    /// </summary>
    internal ConsoleFindView RebuildFind(LaunchRole role, string query, bool matchCase, int anchorLine)
    {
        var st = FindState(role);
        st.Query = query ?? "";
        st.MatchCase = matchCase;
        st.Matches.Clear();
        st.Current = -1;
        st.Capped = false;

        if (st.Query.Length > 0)
        {
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var lines = Lines(role);
            for (var i = 0; i < lines.Count; i++)
            {
                var text = lines[i].Text;
                var at = 0;
                while (at <= text.Length - st.Query.Length)
                {
                    var hit = text.IndexOf(st.Query, at, cmp);
                    if (hit < 0)
                        break;

                    st.Matches.Add(new ConsoleFindMatch(i, hit, st.Query.Length));

                    // Windows stops the whole walk at the cap and marks the
                    // count as a lower bound; a match at the very cap is still
                    // included, so the counter can be one past it.
                    if (st.Matches.Count >= ConsoleFindMaxMatches)
                    {
                        st.Capped = true;
                        break;
                    }

                    at = hit + st.Query.Length;
                }

                if (st.Capped)
                    break;
            }
        }

        if (st.Matches.Count > 0)
            st.Current = IndexAtOrAfter(st.Matches, anchorLine);

        st.Rev = _revisions[(int)role];
        return FindViewFor(role);
    }

    /// <summary>
    /// Move to the next or previous match, wrapping. A stale match set is rebuilt
    /// first — the pane has taken lines since — which is also what re-anchors the
    /// walk, so stepping never lands on a line that has moved.
    /// </summary>
    internal ConsoleFindView StepFind(LaunchRole role, int delta, int anchorLine)
    {
        var st = FindState(role);
        if (st.Matches.Count == 0 || st.Rev != _revisions[(int)role])
        {
            var rebuilt = RebuildFind(role, st.Query, st.MatchCase, anchorLine);
            if (delta == 0 || rebuilt.Count == 0)
                return rebuilt;
        }

        if (st.Matches.Count == 0)
            return FindViewFor(role);

        var n = st.Matches.Count;
        st.Current = st.Current < 0
            ? (delta >= 0 ? 0 : n - 1)
            : (((st.Current + delta) % n) + n) % n;

        return FindViewFor(role);
    }

    /// <summary>The counter line: blank with no query, "no matches", or
    /// "current/total" with a "+" when the cap cut the walk short.</summary>
    internal string FindCounter(LaunchRole role)
    {
        var st = FindState(role);
        if (st.Query.Length == 0)
            return "";
        if (st.Matches.Count == 0)
            return Loc.Get("find_none");

        var total = st.Matches.Count + (st.Capped ? "+" : "");
        return $"{Math.Max(0, st.Current) + 1}/{total}";
    }

    ConsoleFindView FindViewFor(LaunchRole role)
    {
        var st = FindState(role);
        var match = st.Current >= 0 && st.Current < st.Matches.Count
            ? st.Matches[st.Current]
            : (ConsoleFindMatch?)null;
        return new ConsoleFindView(st.Matches.Count, st.Current, st.Capped, FindCounter(role), match);
    }

    /// <summary>
    /// The first match at or after the anchor. Windows wraps to the first match
    /// when the anchor is past every one of them, because its anchor is a live
    /// document position and "past everything" means the player is at the very
    /// bottom. Here the anchor can be the tail of a following pane, where the
    /// newest match is the one in front of the player — so that case wraps to the
    /// last match instead.
    /// </summary>
    static int IndexAtOrAfter(List<ConsoleFindMatch> matches, int anchorLine)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].Line >= anchorLine)
                return i;
        }

        return anchorLine == ConsoleFindTailAnchor ? matches.Count - 1 : 0;
    }

    // ------------------------------------------------------------- launching

    /// <summary>
    /// Start a game role and give its console a tap.
    ///
    /// <para>Two transports, in this order: the relay when it is installed (the game's
    /// own console, carried over a loopback socket the relay bridges to the Windows
    /// pipes the game expects — see <see cref="HostedConsole"/>), and the captured
    /// child when it is not. The second is what this launcher did before the relay
    /// existed, and it is a fallback rather than a substitute: <c>proton run</c>
    /// discards the child's streams, so it reads Proton's own launch chatter and not
    /// the game's console — which is why the player saw an empty pane and two console
    /// windows. It is kept because a relay that is missing must cost a pane, not a
    /// launch.</para>
    ///
    /// <para>The relay is started first and the game second, in that order and not the
    /// other way round: the game may only be started once the pipes exist and it has
    /// been told their names, or its console setup finds nothing and opens a window of
    /// its own.</para>
    /// </summary>
    async Task<(Process Child, ConsoleTap Tap)> StartGameConsoleAsync(
        ProtonLauncher.ProtonRunOptions options, LaunchRole role)
    {
        var hosted = await HostedConsole.StartAsync(options.ProtonDir, options.PrefixPath,
            options.WorkingDirectory, role, AppendLog).ConfigureAwait(true);

        if (hosted is null)
        {
            // Upstream's wipe of the four hosted-console names, so a child that is not
            // hosted cannot open a leftover one — the one thing the fallback path has
            // to do beyond what it always did.
            var cleared = HostedConsole.MergeEnvironment(options.ExtraEnv,
                HostedConsole.ClearedEnvironment());
            return ProtonLauncher.StartTapped(options with { ExtraEnv = cleared }, role);
        }

        try
        {
            var game = hosted.StartGame(options.ExePath, options.Args, options.ExtraEnv);
            AppendLog($"Console relay on 127.0.0.1:{hosted.Port}; the game's console is "
                + $"hosted ({hosted.OutPipePath} / {hosted.InPipePath}).");
            // Non-null by construction: StartGame just made it, and the tap it returns
            // is the one that owns the session (`owned: this`), so detaching the
            // console is what ends the relay.
            return (game, hosted.Tap!);
        }
        catch
        {
            // A game that could not be started must not leave a relay holding a port
            // and a prefix for the rest of the session.
            hosted.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The game's launch path: the same immediate-death detection as
    /// <see cref="LaunchWindowsAsync"/>, but the child's console goes to a
    /// <see cref="ConsoleTap"/> so the Console tab sees it, rather than losing it to
    /// a callback.
    ///
    /// Every game launch comes through here — the client, the dedi, and a join.
    /// The EA paths use <c>LaunchWindowsAsync</c> instead (a log tail and no
    /// console pane). The untapped captured twin that used to sit beside this one
    /// had no callers left once the join moved to the tap, so it is gone.
    /// </summary>
    async Task<bool> LaunchProtonTappedAsync(ProtonLauncher.ProtonRunOptions options,
        LaunchRole role, string label, string runningNote, int graceSeconds = 15)
    {
        var started = DateTime.UtcNow;
        var lines = new List<string>();
        var gate = new object();

        var (child, tap) = await StartGameConsoleAsync(options, role).ConfigureAwait(true);

        // The tail first, then the console: attaching is what starts the tap
        // reading, so anything subscribed afterwards could miss the child's
        // first line — and the first line is often the reason it failed.
        tap.LineReceived += line =>
        {
            // The tail is the launch diagnosis: Wine writes a fatal GL failure
            // and then either exits or runs headless, and only its own words
            // distinguish the two.
            lock (gate)
            {
                lines.Add(line);
                if (lines.Count > 40) lines.RemoveAt(0);
            }
            AppendLogThreadSafe($"{label}: {ConsoleTap.StripAnsi(line)}");
        };
        AttachConsole(role, tap);

        var exitTask = child.WaitForExitAsync();
        var done = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(graceSeconds)));
        if (ReferenceEquals(done, exitTask))
        {
            try { await exitTask; } catch { }
            var code = child.ExitCode;
            List<string> tail;
            lock (gate) tail = new List<string>(lines);
            var gl = tail.Exists(ProtonLauncher.LooksLikeGlFailure);
            AppendLog(EaInstallWatch.EarlyExitNote(label, DateTime.UtcNow - started, code));
            if (tail.Count == 0)
                AppendLog($"{label}: no output from Proton. Check the Proton build and prefix.");
            if (gl)
                AppendLog($"{label}: Wine could not create an OpenGL context (GLX), so its "
                          + "window can never appear. Verify with `glxinfo -B`; an X11 "
                          + "session or a working XWayland GLX is required.");
            // The console stays readable: the child's own last words are the
            // record of how it ended.
            child.Dispose();
            DetachConsole(role);
            return false;
        }

        if (!child.HasExited)
            AppendLog(runningNote);

        // Deliberately not disposed on this path. The handle is how the launcher
        // later asks whether the child is still there (<see cref="LocalDediAlive"/>,
        // which the steering and the host heartbeat both gate on), and a disposed
        // Process cannot be asked anything. It goes away with the child's own
        // finalizer, which is what Windows' launcher relies on for the same
        // question through ProcessSpawner.
        return true;
    }

    // ------------------------------------------------------------- steering
    //
    // Port of Windows' local-RCON steering: TrySelectedChangePair
    // (MainWindow.xaml.cs:4861), ChangeMapPending / PlaylistOnlyChangePending
    // (:4899 / :4917), ChangeTargetMapLabel (:4947), PlaylistDisplayName
    // (:4958), ApplyModeAsync (:5172), OnReloadLevel (:5134) and
    // NoteMapChangeReady (:5248). The session is LocalRconSession (a verbatim
    // port in Linux.Core): the launcher mints a password, puts it in the dedi's
    // environment -- R5F_LOCAL_RCON* is what makes the dedi listen on the
    // loopback -- and steers the live level with bridge_setmode over netcon.

    LocalRconSession? _localRcon;
    string? _livePlaylist;
    string? _liveMap;
    bool _changeMapBusy;
    string? _pendingChangeMap;
    string? _pendingChangeLabel;

    /// <summary>Windows' <c>_localRcon is not null</c>: a hosted dedi this
    /// launcher can steer -- "PLAY from here", never a browser join.</summary>
    public bool HostSteeringReady => _localRcon is not null;

    public bool ChangeMapBusy => _changeMapBusy;

    /// <summary>The playlist+map the hosted level is on, as last set: what the
    /// dedi actually launched with, then whatever the steering last landed.
    /// The ready line has to name the live map, otherwise a lobby boot would
    /// pass for a live match.</summary>
    public string? LivePlaylist => _livePlaylist;
    public string? LiveMap => _liveMap;

    /// <summary>
    /// Remember what the host was launched with. Windows sets this at spawn
    /// (RunPlayAsync :2550, OnPlayDedi :2345) so the ready line can be matched
    /// against the map that was asked for and so CHANGE MAP can tell "same
    /// level" from "different level".
    /// </summary>
    internal void SetLivePair(string? playlist, string? map)
    {
        _livePlaylist = playlist;
        _liveMap = map;
        OnPropertyChanged(nameof(LivePlaylist));
        OnPropertyChanged(nameof(LiveMap));
    }

    /// <summary>
    /// Mint the session for a launch and hand it to the child. Windows mints
    /// before the spawn for the same reason: the dedi's own environment is what
    /// tells it to open the loopback listener, so a session created afterwards
    /// would never be answered.
    /// </summary>
    internal LocalRconSession OpenRcon(int gamePort)
    {
        _localRcon?.Dispose();
        _localRcon = LocalRconSession.Create(gamePort);
        OnPropertyChanged(nameof(HostSteeringReady));
        return _localRcon;
    }

    /// <summary>Windows' DropLocalRcon: the session dies with the process it
    /// steered, and the live pair dies with it -- there is nothing left to
    /// reload. The match is disarmed here too: on Windows the two are separate
    /// (a kill path calls one, a crash path the other), but on this side the
    /// session is the only handle the launcher has on "a hosted match is
    /// running", so the moment it goes there is nothing left to watch either.</summary>
    internal void DropLocalRcon()
    {
        _localRcon?.Dispose();
        _localRcon = null;
        _livePlaylist = null;
        _liveMap = null;
        ClearPendingChangeMap();
        DisarmHostedMatch();
        OnPropertyChanged(nameof(HostSteeringReady));
        OnPropertyChanged(nameof(LivePlaylist));
        OnPropertyChanged(nameof(LiveMap));
    }

    /// <summary>
    /// What the current selection means as a level change -- Windows'
    /// TrySelectedChangePair, refusals included: a lobby is not a level to drop
    /// into, and TryBuildSetMode is the same no-quotes-no-semicolons check the
    /// launch line gets, because this is a command line either way.
    /// </summary>
    internal bool TrySelectedChangePair(out string playlist, out string map)
    {
        var pair = SelectedPlayPair();
        playlist = pair.Playlist;
        map = pair.Map;

        if (ModeCardViewModel.IsLobbyPlaylist(playlist) || IsLobbyStem(map))
            return false;

        return LocalRcon.TryBuildSetMode(playlist, map, out _, out _);
    }

    /// <summary>
    /// A dedi of ours that is still running. Windows asks
    /// ProcessSpawner.IsRoleAlive, which reads a named event inside the prefix;
    /// this side owns the child, so the tap it reads and the child's own process
    /// handle answer the same question — and answer it the same way Windows'
    /// does, because a child that has exited reads as gone whether or not the
    /// pane has been detached yet.
    /// </summary>
    internal bool LocalDediAlive()
    {
        var tap = _tapServer;
        if (tap is null)
            return false;
        try
        {
            return !tap.Child.HasExited;
        }
        catch
        {
            // No handle left to ask: treat it as gone rather than as alive.
            return false;
        }
    }

    /// <summary>Is there a change worth sending? Windows' ChangeMapPending.</summary>
    internal bool ChangeMapPending()
    {
        if (_localRcon is null || _changeMapBusy)
            return false;
        if (!LocalDediAlive())
            return false;
        if (!TrySelectedChangePair(out var playlist, out var map))
            return false;
        return !(string.Equals(playlist, _livePlaylist, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(map, _liveMap, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the pick keeps the live map and only moves the mode,
    /// so the action reads CHANGE PLAYLIST instead of CHANGE MAP.</summary>
    internal bool PlaylistOnlyChangePending()
    {
        if (string.IsNullOrEmpty(_liveMap))
            return false;
        if (!TrySelectedChangePair(out var playlist, out var map))
            return false;
        return string.Equals(map, _liveMap, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(playlist, _livePlaylist, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A mode card names the playlist better than the id does.</summary>
    string PlaylistDisplayName(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return string.Empty;
        foreach (var card in ModeCards)
        {
            if (string.Equals(card.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase))
                return card.Title;
        }
        foreach (var mode in Catalog.Modes)
        {
            if (string.Equals(mode.Id, playlistId, StringComparison.OrdinalIgnoreCase))
                return ModeCardViewModel.LocalizedTitle(mode.Id, mode.Title);
        }
        return playlistId;
    }

    string MapDisplayName(string stem) =>
        MapLabels.PlayerName(stem, CatalogMapNames);

    /// <summary>The map the pending change would land on, in the player's own
    /// name for it -- Windows' ChangeTargetMapLabel.</summary>
    string ChangeTargetMapLabel()
    {
        if (!TrySelectedChangePair(out _, out var map))
            return string.Empty;
        if (IsSimpleMode && SelectedMode?.SelectedMap is { } pick &&
            string.Equals(pick.Stem, map, StringComparison.OrdinalIgnoreCase))
        {
            return pick.DisplayName;
        }
        return MapDisplayName(map);
    }

    /// <summary>
    /// Console-tab Reload sends the *live* playlist+map back through the same
    /// changelevel path, which is how you restart the level you are on. CHANGE
    /// MAP stays for a different pick. Windows' OnReloadLevel.
    /// </summary>
    internal async Task ReloadLevelAsync()
    {
        if (_localRcon is null || _changeMapBusy)
            return;

        string playlist;
        string map;
        if (!string.IsNullOrEmpty(_livePlaylist) && !string.IsNullOrEmpty(_liveMap))
        {
            playlist = _livePlaylist;
            map = _liveMap;
        }
        else if (!TrySelectedChangePair(out playlist, out map))
        {
            SimpleStatus = Loc.Get("status_reload_level_none");
            return;
        }

        if (ModeCardViewModel.IsLobbyPlaylist(playlist) || IsLobbyStem(map))
        {
            SimpleStatus = Loc.Get("status_reload_level_none");
            return;
        }

        if (!LocalRcon.TryBuildSetMode(playlist, map, out _, out _))
        {
            SimpleStatus = Loc.Get("status_reload_level_none");
            return;
        }

        await ApplyModeAsync(playlist, map, MapDisplayName(map), reload: true).ConfigureAwait(true);
    }

    /// <summary>The CHANGE MAP action: Windows' OnChangeMap, over the pair the
    /// current selection means.</summary>
    internal async Task ChangeMapAsync()
    {
        if (_localRcon is null || _changeMapBusy)
            return;
        if (!TrySelectedChangePair(out var playlist, out var map))
        {
            SimpleStatus = Loc.Get("status_change_map_pick");
            return;
        }

        var playlistOnly = PlaylistOnlyChangePending();
        var label = ChangeTargetMapLabel();
        if (string.IsNullOrWhiteSpace(label))
            label = MapDisplayName(map);
        await ApplyModeAsync(playlist, map, label, reload: false, playlistOnly).ConfigureAwait(true);
    }

    /// <summary>
    /// Send one mode/map change and report it. Windows' ApplyModeAsync: the
    /// pending pair is held until the dedi's own map-ready line says the level
    /// landed (NoteMapChangeReady), because a changelevel is asynchronous on the
    /// server side and the launcher is not the one that finishes it.
    /// </summary>
    async Task ApplyModeAsync(string playlist, string map, string label, bool reload,
        bool playlistOnly = false)
    {
        if (_localRcon is null || _changeMapBusy)
            return;

        var modeLabel = PlaylistDisplayName(playlist);
        _changeMapBusy = true;
        _pendingChangeMap = map;
        _pendingChangeLabel = label;
        OnPropertyChanged(nameof(ChangeMapBusy));
        SimpleStatus = reload
            ? Loc.Format("status_reloading_level", label)
            : playlistOnly
                ? Loc.Format("status_changing_playlist", modeLabel)
                : Loc.Format("status_changing_map", label);
        Status = (reload
            ? "Reloading " + label
            : playlistOnly
                ? "Switching playlist to " + modeLabel
                : "Changing map to " + label) + "…";
        try
        {
            var session = _localRcon;
            var result = await Task.Run(() => session.SetMode(playlist, map)).ConfigureAwait(true);
            if (result.Ok)
            {
                _livePlaylist = playlist;
                _liveMap = map;
                OnPropertyChanged(nameof(LivePlaylist));
                OnPropertyChanged(nameof(LiveMap));
                AppendLog((reload ? "Reload level: " : "Change map: ") + playlist + " " + map);
            }
            else
            {
                ClearPendingChangeMap();
                SimpleStatus = Loc.Get(reload
                    ? "status_reload_level_loading"
                    : playlistOnly
                        ? "status_change_playlist_loading"
                        : "status_change_map_loading");
                Status = reload
                    ? "Reload failed"
                    : playlistOnly ? "Playlist change failed" : "Map change failed";
                AppendLog((reload ? "Reload level failed: " : "Change map failed: ")
                    + (result.Error ?? "unknown"));
            }
        }
        catch (Exception ex)
        {
            ClearPendingChangeMap();
            SimpleStatus = Loc.Get(reload
                ? "status_reload_level_failed"
                : playlistOnly
                    ? "status_change_playlist_failed"
                    : "status_change_map_failed");
            AppendLog((reload ? "Reload level failed: " : "Change map failed: ") + ex.Message);
        }
        finally
        {
            _changeMapBusy = false;
            OnPropertyChanged(nameof(ChangeMapBusy));
        }
    }

    void ClearPendingChangeMap()
    {
        _pendingChangeMap = null;
        _pendingChangeLabel = null;
    }

    /// <summary>
    /// The level the pending change asked for is live: the status line stops
    /// saying "changing" and starts saying what is playing. Windows'
    /// NoteMapChangeReady, which hops to the UI thread because the line arrives
    /// on the tap's reader thread.
    /// </summary>
    void NoteMapChangeReady()
    {
        Invoke(() =>
        {
            var map = _pendingChangeMap;
            if (string.IsNullOrEmpty(map))
                return;

            var label = _pendingChangeLabel;
            if (string.IsNullOrWhiteSpace(label))
                label = MapDisplayName(map);
            ClearPendingChangeMap();

            // The log line first: this port's AppendLog writes the status line as
            // well as the log, and the words that must survive are the two
            // Windows sets here (SetSimpleStatus for the player's own name for
            // the level, UpdateStatus for the playlist/map pair). Windows logs
            // nothing at this point; the line is kept because the console's log
            // is where a player reads what the dedi was told to do.
            AppendLog($"Playing {_livePlaylist} {map}");
            SimpleStatus = Loc.Format("status_playing_map", label);
            Status = "Playing  |  " + (_livePlaylist ?? "") + " " + map;
        });
    }

    [RelayCommand]
    private async Task ReloadLevel() => await ReloadLevelAsync();

    [RelayCommand]
    private async Task SendChangeMap() => await ChangeMapAsync();

    // ------------------------------------------------------ the host heartbeat
    //
    // Port of Windows' watchdog (MainWindow.xaml.cs:3466-3573). The dedi answers
    // RCON from its own frame loop, so "the process is still running" says
    // nothing about the match: a script that hangs the frame loop leaves a dedi
    // that is alive, listening and never answering again. Netcon is the only
    // question the launcher can put to it -- and a ping is a full connect+auth
    // the dedi logs -- which is why the cadence is coarse, the ping is short,
    // and the window a level load gets is generous. Windows runs these ticks off
    // its two-second process watch; this port runs them off its own
    // (MainWindow's ProcWatchTimer), one TickHostHeartbeat call per tick, with
    // the ten-second spacing enforced here rather than by the timer.

    /// <summary>Windows' HostHeartbeatEvery. Each ping is a full RCON
    /// connect+auth the dedi logs, so it stays coarse.</summary>
    static readonly TimeSpan HostHeartbeatEvery = TimeSpan.FromSeconds(10);

    /// <summary>Windows' HostHeartbeatMissLimit: eight misses in a row is eighty
    /// seconds of a server that has not answered.</summary>
    const int HostHeartbeatMissLimit = 8;

    /// <summary>Windows' HostHeartbeatGrace: what a level load gets. The frame
    /// loop stops for the whole load, and a big map can take a minute.</summary>
    static readonly TimeSpan HostHeartbeatGrace = TimeSpan.FromSeconds(90);

    /// <summary>Windows' own readings: 15 s after the host reports a live level
    /// (:2609), 10 s once a ready line says the level is up (:3571), and 1.5 s
    /// for the ping itself (:3498).</summary>
    static readonly TimeSpan HostReadyGrace = TimeSpan.FromSeconds(15);
    static readonly TimeSpan HostLevelReadyGrace = TimeSpan.FromSeconds(10);
    static readonly TimeSpan HostHeartbeatPingTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>Windows' ten-second throttle on the "not answering" log line: a
    /// server that is gone misses forever, and one line per miss would be eighty
    /// lines of the same sentence.</summary>
    static readonly TimeSpan HostHeartbeatLogEvery = TimeSpan.FromSeconds(10);

    /// <summary>Is this launcher hosting a match right now? Windows'
    /// <c>_hostedLocalMatch</c>: armed when Play Local's dedi is up
    /// (<see cref="ArmHostedMatch"/>) and disarmed with the session it steers
    /// (<see cref="DropLocalRcon"/>). A dedi launched on its own from the
    /// Advanced tab is steerable but never armed -- Windows arms only in its
    /// local-play path, and the watchdog follows the match, not the process.</summary>
    bool _hostedLocalMatch;

    /// <summary>When the watchdog may ask again, and the answer to "has this host
    /// ever reported a live level?" at the same time. <c>MaxValue</c> means it
    /// has not: the watchdog stays quiet for a dedi that is still booting (a
    /// boot is not a match yet), and the teardown rule in <see cref="NoteDediLine"/>
    /// reads the same value to tell an error during boot -- a launch that failed
    /// -- from an error that ended a match.</summary>
    DateTime _hostHeartbeatGraceUntilUtc = DateTime.MaxValue;
    DateTime _hostHeartbeatNextUtc;
    int _hostHeartbeatMisses;
    DateTime _hostHeartbeatLastLogUtc;
    int _hostHeartbeatBusy;

    /// <summary>How a hosted match ended badly (Windows' HostedServerFault).
    /// ClientLost is the client's own disconnect dialog; this port does not
    /// classify that line into a fault yet -- <see cref="NoteClientLine"/> only
    /// reports what the game said -- so the member is here for the lane, not for
    /// a caller.</summary>
    internal enum HostedServerFault
    {
        Crashed,
        Hung,
        ClientLost,
    }

    /// <summary>One fault per match: Windows' <c>_crashPromptPosted</c>, the
    /// single-shot gate the heartbeat and the dedi's own fatal lines share.</summary>
    int _faultPosted;

    /// <summary>Set while the window is going away, so a ping that lands after
    /// that does not try to paint (Windows' <c>_windowClosing</c>).</summary>
    internal bool WindowClosing { get; set; }

    /// <summary>
    /// Windows' ArmHostedMatch, minus the crash-prompt state this port does not
    /// have. The grace is set to <c>MaxValue</c> on purpose: a match is armed
    /// before its host has said anything, and the watchdog must not start asking
    /// a dedi that is still booting whether it has stopped answering.
    /// </summary>
    internal void ArmHostedMatch()
    {
        _hostedLocalMatch = true;
        Interlocked.Exchange(ref _faultPosted, 0);
        _hostHeartbeatGraceUntilUtc = DateTime.MaxValue;
        _hostHeartbeatMisses = 0;
    }

    /// <summary>Windows' DisarmHostedMatch: no match of ours is running, so
    /// there is nothing to watch and nothing to warn about.</summary>
    internal void DisarmHostedMatch() => _hostedLocalMatch = false;

    /// <summary>State the suite reads back: whether a match is armed, and how
    /// many pings in a row have gone unanswered.</summary>
    internal bool HostedMatchArmed => _hostedLocalMatch;
    internal int HostHeartbeatMisses => _hostHeartbeatMisses;

    /// <summary>Windows' TickHostHeartbeat, off the window's timer.</summary>
    internal void TickHostHeartbeat() => TickHostHeartbeat(DateTime.UtcNow);

    /// <summary>
    /// The rule, with the clock passed in rather than read, so the suite can
    /// state a time instead of waiting ten seconds for the timer's own
    /// (Windows reads <c>DateTime.UtcNow</c> in here and ticks every two
    /// seconds; the ten-second spacing is what makes the reading testable).
    /// The ping runs on a background task because it blocks for up to its
    /// timeout, and it is single-flight because a server that is not answering
    /// takes the whole timeout to say so.
    /// </summary>
    internal void TickHostHeartbeat(DateTime now)
    {
        if (!HostHeartbeatDue(now))
            return;
        if (Interlocked.CompareExchange(ref _hostHeartbeatBusy, 1, 0) != 0)
            return;

        var rcon = _localRcon;
        _ = Task.Run(() =>
        {
            bool? alive = null;
            try
            {
                // Asked only of a process this launcher started and that is
                // still there: Windows gates this on ProcessSpawner.IsRoleAlive
                // for the same reason, and a dedi that has exited is the
                // process watch's business rather than the watchdog's -- upstream
                // leaves `alive` null there and notes nothing.
                if (rcon is not null && LocalDediAlive())
                    alive = rcon.Ping(HostHeartbeatPingTimeout).Ok;
            }
            catch
            {
                alive = null;
            }
            finally
            {
                Interlocked.Exchange(ref _hostHeartbeatBusy, 0);
            }

            if (alive is { } a && !WindowClosing)
                Post(() => NoteHostHeartbeat(a, DateTime.UtcNow));
        });
    }

    /// <summary>
    /// Is a ping owed? Windows' HostHeartbeatDue, clause for clause: only while
    /// this launcher is hosting, only with a session to ask through, never while
    /// a mode or map change is in flight (the dedi is reloading then, which is a
    /// legitimate silence), never after a fault has been reported, and never
    /// before the grace has run out.
    /// </summary>
    internal bool HostHeartbeatDue(DateTime now)
    {
        if (WindowClosing || !_hostedLocalMatch || _localRcon is null)
            return false;
        if (_changeMapBusy || _pendingChangeMap is not null)
            return false;
        if (Volatile.Read(ref _faultPosted) != 0)
            return false;
        if (now < _hostHeartbeatGraceUntilUtc || now < _hostHeartbeatNextUtc)
            return false;
        _hostHeartbeatNextUtc = now + HostHeartbeatEvery;
        return true;
    }

    /// <summary>
    /// One ping's answer, on the UI thread. Windows' NoteHostHeartbeat: an
    /// answer resets the count, a miss is logged at most every ten seconds, and
    /// the eighth miss in a row is a hung frame loop -- the one fault the
    /// process watch cannot see, because the dedi is still there.
    /// </summary>
    internal void NoteHostHeartbeat(bool alive, DateTime now)
    {
        if (alive)
        {
            _hostHeartbeatMisses = 0;
            return;
        }

        _hostHeartbeatMisses++;
        if (now - _hostHeartbeatLastLogUtc > HostHeartbeatLogEvery)
        {
            _hostHeartbeatLastLogUtc = now;
            // The log only: Windows writes this through Log(), and the launcher's
            // status line is not a place for a miss counter.
            LogOnly($"Play Local: server not answering RCON "
                + $"({_hostHeartbeatMisses}/{HostHeartbeatMissLimit})");
        }

        if (_hostHeartbeatMisses < HostHeartbeatMissLimit)
            return;

        QueueHostedServerFault(HostedServerFault.Hung);
    }

    /// <summary>
    /// Windows' QueueHostedServerCrash: one fault per match, handled on the UI
    /// thread. What the player gets is what this port's fault lane has always
    /// given -- the status line, the log, and a note in the server pane's own
    /// voice; the modal warning Windows puts on top of that is the console tab's
    /// remaining open item -- plus the teardown Windows does around the prompt:
    /// the match is disarmed, the dedi is killed and the session it was steered
    /// through drops with it. A ClientLost is the one fault that leaves the
    /// server running, because the client is what went away.
    ///
    /// The excerpt is read first, on purpose: it is the only thing that can say
    /// <em>which</em> script error ended the match, and everything below clears
    /// the state it is read from.
    /// </summary>
    internal void QueueHostedServerFault(HostedServerFault fault, string? clientMessage = null)
    {
        if (WindowClosing || !_hostedLocalMatch)
            return;
        if (Interlocked.CompareExchange(ref _faultPosted, 1, 0) != 0)
            return;

        var excerpt = RecentScriptError();
        var statusKey = fault switch
        {
            HostedServerFault.Hung => "status_server_hung",
            HostedServerFault.ClientLost => "status_client_lost_server",
            _ => "status_server_crashed",
        };
        var note = fault switch
        {
            HostedServerFault.ClientLost =>
                Loc.Format("warn_client_lost_server", clientMessage ?? string.Empty),
            HostedServerFault.Hung => excerpt is null
                ? Loc.Get("warn_server_hung")
                : Loc.Format("warn_server_hung_script", excerpt),
            _ => excerpt is null
                ? Loc.Get("warn_server_crashed")
                : Loc.Format("warn_server_crashed_script", excerpt),
        };

        Invoke(() =>
        {
            AppendLog($"Dedi fault ({fault}): {excerpt ?? Loc.Get(statusKey)}");

            if (fault == HostedServerFault.ClientLost)
            {
                AppendLog("Play Local: client dropped, server still answers, left it running.");
            }
            else
            {
                DisarmHostedMatch();
                // The dedi is still alive -- that is what "hung" means -- so it
                // has to be killed rather than left holding the port. Windows
                // kills the role by its tracked pids; this port's kill path is
                // the same one the Kill button uses, and it also drops the
                // session, ends a readiness wait, and detaches the pane. With no
                // tap attached there is nothing of ours to kill, so the session
                // is dropped on its own.
                if (ServerConsoleLive)
                    KillDedi();
                else
                    DropLocalRcon();
            }

            // The note, and then the statuses, in that order and last: this
            // port's AppendLog writes the status line as well as the log, and
            // both the kill path above and the window's note handler go through
            // it -- so what the player is left reading has to be written after
            // them. (Windows writes its status first because its Log leaves the
            // status alone.)
            ConsoleNoted?.Invoke(LaunchRole.Dedicated, note);
            SimpleStatus = Loc.Get(statusKey);
            Status = Loc.Get(statusKey);
        });
    }
}
