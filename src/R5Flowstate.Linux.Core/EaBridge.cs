using System.Diagnostics;

namespace R5Flowstate.Linux.Core;

/// <summary>Where the channel between the game and the EA App stands.</summary>
public enum EaBridgeState
{
    /// <summary>Nothing has been asked of it yet.</summary>
    NotChecked,

    /// <summary>There is no EA App to reach — the player has to install it.</summary>
    EaMissing,

    /// <summary>The EA prefix is being built (wineboot), which is slow and is not
    /// the same as a failure.</summary>
    Bootstrapping,

    /// <summary>The EA App was started and is being waited for.</summary>
    Starting,

    /// <summary>Something is listening on the LSX port.</summary>
    Listening,

    /// <summary>EA is up (or was started) but the port the game dials is not
    /// answering, so a join would go into a handshake hold.</summary>
    Refused,

    /// <summary>EA was there and is gone. Later joins are refused rather than
    /// launched into a hold.</summary>
    Died,

    /// <summary>EA is running and the launcher cannot see past the socket: no
    /// listener on the LSX port, but nothing said so either.</summary>
    Unverified,
}

/// <summary>What one gate pass found. <see cref="Line"/> is the sentence the card
/// and the log show; <see cref="CanJoin"/> is the answer to "may this launch
/// proceed with online auth".</summary>
public sealed record EaBridgeReport(
    EaBridgeState State,
    bool CanJoin,
    string Line,
    bool OwnedByLauncher = false,
    int? Pid = null)
{
    public bool Ok => State == EaBridgeState.Listening;
}

/// <summary>
/// The gate that stands between "the player pressed Join" and a client that
/// cannot prove its account.
///
/// The game reaches the EA App over the host's loopback (<see cref="EaChannelProbe"/>),
/// which works across prefixes — but only if something is on the other end. Wine
/// writes launch failures to stderr and then runs headless, and a client started
/// into a closed channel prints <c>[NET-OBS] HANDSHAKE hold</c> and waits, which
/// looks exactly like a slow join. So the launcher asks first, and refuses with the
/// reason instead.
///
/// <para><b>Two facts are polled, not one.</b> The child's own
/// <c>HasExited</c> — which the socket cannot see, since a dead process can leave
/// a listener in TIME_WAIT — and a re-probe of the port. A process that is alive
/// with a closed port is a state a player can reach (EA's LSX server can die on its
/// own), and it is the one that must not read as "fine".</para>
///
/// <para><b>Only a child the launcher started is ever relaunched.</b> If the EA App
/// was already running — the player used the menu entry, which is the normal way —
/// this watches and verifies it and never touches it: a second EA App on the same
/// socket is how a working setup gets broken. A second death is final.</para>
/// </summary>
public static class EaBridge
{
    /// <summary>How long to wait for EA's LSX port to open after starting it. The
    /// EA App is a Chromium client: it has to start, sign in from its stored
    /// session, and only then bind 3216.</summary>
    public static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(750);

    /// <summary>How long a channel that was up may stay unanswerable before it is
    /// called dead.</summary>
    public static readonly TimeSpan DeathGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Bring the channel up (or find it already up) and say whether a launch may go
    /// online. Never throws; every ending is a state.
    /// </summary>
    /// <param name="eaDesktopExe">The EA App's exe, or null when the prefix has
    /// none — which is <see cref="EaBridgeState.EaMissing"/> and a refusal.</param>
    /// <param name="start">Starts the EA App, or null in a dry run (the
    /// <c>--ea-probe</c> lane and the suite pass null: nothing is ever started by a
    /// probe).</param>
    public static async Task<EaBridgeReport> EnsureAsync(
        string? eaDesktopExe,
        Func<Process>? start,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        log ??= _ => { };

        // Already up? Then the player started it, and nothing here may touch it.
        var open = EaChannelProbe.Probe();
        if (EaChannelProbe.IsListening(open))
        {
            var (line, _) = EaChannelProbe.Verdict(open);
            log("EA App is already listening — leaving it alone.");
            return new EaBridgeReport(EaBridgeState.Listening, true, line);
        }

        if (string.IsNullOrWhiteSpace(eaDesktopExe))
        {
            return new EaBridgeReport(EaBridgeState.EaMissing, false,
                "identity: no EA App to reach — install it before joining online.");
        }

        if (start is null)
        {
            var (line, _) = EaChannelProbe.Verdict(open);
            return new EaBridgeReport(EaBridgeState.Refused, false, line);
        }

        Process? child = null;
        var startedAt = DateTime.UtcNow;
        try
        {
            log($"Starting the EA App to open the channel: {eaDesktopExe}");
            child = start();
        }
        catch (Exception ex)
        {
            return new EaBridgeReport(EaBridgeState.Died, false,
                $"identity: the EA App could not be started ({ex.Message}).");
        }

        var pid = SafePid(child);
        var deadline = DateTime.UtcNow + ListenTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
                break;

            var probes = EaChannelProbe.Probe();
            if (EaChannelProbe.IsListening(probes))
            {
                var (line, _) = EaChannelProbe.Verdict(probes);
                log("The EA App opened its LSX port.");
                return new EaBridgeReport(EaBridgeState.Listening, true, line, true, pid);
            }

            if (child is not null && child.HasExited)
            {
                return new EaBridgeReport(EaBridgeState.Died, false,
                    "identity: " + EaInstallWatch.EarlyExitNote("The EA App",
                        DateTime.UtcNow - startedAt, SafeExitCode(child)), true, pid);
            }

            try { await Task.Delay(PollEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        return new EaBridgeReport(EaBridgeState.Refused, false,
            $"identity: the EA App is running but nothing opened port {EaChannelProbe.LsxPort} "
            + $"within {(int)ListenTimeout.TotalSeconds}s. Sign in to it, or press Probe EA "
            + "channel to see what is listening.", true, pid);
    }

    static int? SafePid(Process? p)
    {
        try { return p?.Id; } catch { return null; }
    }

    /// <summary>One word per state, for the card and the log.</summary>
    public static string Describe(EaBridgeState state) => state switch
    {
        EaBridgeState.NotChecked => "not checked",
        EaBridgeState.EaMissing => "no EA App",
        EaBridgeState.Bootstrapping => "building the EA prefix",
        EaBridgeState.Starting => "starting the EA App",
        EaBridgeState.Listening => "listening",
        EaBridgeState.Refused => "running, port closed",
        EaBridgeState.Died => "the EA App is gone",
        _ => "unverified",
    };

    static int SafeExitCode(Process? p)
    {
        try { return p?.ExitCode ?? -1; } catch { return -1; }
    }
}

/// <summary>
/// Watches a channel that was up. Built for the state the socket cannot see: the
/// EA App's process is gone (or its LSX server died) while the game is still
/// connected to a server with no way to prove who it is.
///
/// One bounded relaunch, and only for a child this launcher started; anything else
/// is reported, never restarted. A second death ends it.
/// </summary>
public sealed class EaBridgeWatcher
{
    readonly Func<Process>? _start;
    readonly Action<string> _log;
    readonly TimeSpan _grace;
    DateTime _firstMissing = DateTime.MinValue;
    bool _relaunched;

    /// <param name="deathGrace">Overridable only so a test can reach the death path
    /// without sleeping for <see cref="EaBridge.DeathGrace"/>; the launcher never
    /// passes it.</param>
    public EaBridgeWatcher(EaBridgeReport report, Func<Process>? start, Action<string>? log = null,
        TimeSpan? deathGrace = null)
    {
        _start = start;
        _log = log ?? (_ => { });
        _grace = deathGrace ?? EaBridge.DeathGrace;
        OwnedByLauncher = report.OwnedByLauncher;
    }

    public bool OwnedByLauncher { get; }

    /// <summary>True once the channel has been declared dead for good.</summary>
    public bool Dead { get; private set; }

    public string? LastLine { get; private set; }

    /// <summary>
    /// One poll. Returns the current state, and sets <see cref="Dead"/> when the
    /// channel is gone and will not be brought back.
    /// </summary>
    public EaBridgeState Poll()
    {
        if (Dead)
            return EaBridgeState.Died;

        var probes = EaChannelProbe.Probe();
        if (EaChannelProbe.IsListening(probes))
        {
            _firstMissing = DateTime.MinValue;
            return EaBridgeState.Listening;
        }

        if (_firstMissing == DateTime.MinValue)
        {
            _firstMissing = DateTime.UtcNow;
            return EaBridgeState.Listening;   // a blip: EA restarts its listener
        }

        if (DateTime.UtcNow - _firstMissing < _grace)
            return EaBridgeState.Listening;

        // Gone for good enough to act on. A child we started, that has not been
        // relaunched yet, gets one attempt; anything else is reported.
        if (OwnedByLauncher && !_relaunched && _start is not null)
        {
            _relaunched = true;
            try
            {
                _log("The EA App's channel closed — restarting it once.");
                _start();
                _firstMissing = DateTime.MinValue;
                return EaBridgeState.Starting;
            }
            catch (Exception ex)
            {
                _log("The EA App could not be restarted: " + ex.Message);
            }
        }

        Dead = true;
        LastLine = $"identity: the EA App's channel closed and did not come back — joins are "
                   + "refused until it is running and signed in again.";
        _log(LastLine);
        return EaBridgeState.Died;
    }
}
