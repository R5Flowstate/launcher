using System.Diagnostics;
using System.Threading;

namespace R5Flowstate.Spawn;

public enum HostReadyWait
{
    Ready,
    ProcessDied,
    Fatal,
    Cancelled,
}

/// <summary>
/// Local-play rendezvous: launcher creates a named event, dedi signals it
/// after the level is live. Console lines are a fallback for older dedi.
/// Wait has no timeout -- process death / fatal / cancel are the only exits.
/// </summary>
public sealed class HostReadyGate : IDisposable
{
    public const string EnvName = "R5F_HOST_READY";
    public const string EventPrefix = @"Local\r5f-host-";
    public const string ReadyTag = "[R5F-HOST] ready";
    public const string FallbackTag = "[s21-dedi] ODL precache replay";
    public const string ScriptErrorTag = "SCRIPT ERROR:";

    private readonly EventWaitHandle _event;
    private int _signaled;
    private int _fatal;
    private int _disposed;

    public string EventName { get; }

    private HostReadyGate(string eventName, EventWaitHandle wait)
    {
        EventName = eventName;
        _event = wait;
    }

    public static HostReadyGate Create()
    {
        var name = EventPrefix + Guid.NewGuid().ToString("N");
        var wait = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        return new HostReadyGate(name, wait);
    }

    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvName] = EventName,
        };
    }

    public void SignalFromConsole()
    {
        if (_disposed != 0)
            return;
        Interlocked.Exchange(ref _signaled, 1);
        try { _event.Set(); }
        catch { /* disposed */ }
    }

    public void SignalFatal()
    {
        if (_disposed != 0)
            return;
        Interlocked.Exchange(ref _fatal, 1);
        try { _event.Set(); }
        catch { /* disposed */ }
    }

    public HostReadyWait Wait(Process dedi, CancellationToken ct)
    {
        while (true)
        {
            if (Volatile.Read(ref _fatal) != 0)
                return HostReadyWait.Fatal;
            if (Volatile.Read(ref _signaled) != 0)
                return HostReadyWait.Ready;
            if (ct.IsCancellationRequested)
                return HostReadyWait.Cancelled;

            try
            {
                if (dedi.HasExited)
                    return HostReadyWait.ProcessDied;
            }
            catch (InvalidOperationException)
            {
                return HostReadyWait.ProcessDied;
            }

            int idx;
            try
            {
                idx = WaitHandle.WaitAny(new WaitHandle[] { _event, ct.WaitHandle }, 250);
            }
            catch (ObjectDisposedException)
            {
                return HostReadyWait.Cancelled;
            }

            if (Volatile.Read(ref _fatal) != 0)
                return HostReadyWait.Fatal;
            if (idx == 0 || Volatile.Read(ref _signaled) != 0)
                return HostReadyWait.Ready;
            if (ct.IsCancellationRequested || idx == 1)
                return HostReadyWait.Cancelled;
        }
    }

    public static bool IsReadyLine(string? line, string? wantMap = null)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        var s = HostedConsoleTap.StripAnsi(line);
        if (s.Contains(FallbackTag, StringComparison.OrdinalIgnoreCase))
            return true;

        var at = s.IndexOf(ReadyTag, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return false;
        if (string.IsNullOrWhiteSpace(wantMap))
            return true;
        return s.IndexOf(wantMap.Trim(), at, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Changelevel complete for this stem. The tagged ready line only -- the
    /// ODL-replay fallback has no map name, so it cannot tell map A from map B.
    /// </summary>
    public static bool IsMapReadyLine(string? line, string? map)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(map))
            return false;

        var s = HostedConsoleTap.StripAnsi(line);
        var at = s.IndexOf(ReadyTag, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return false;
        return s.IndexOf(map.Trim(), at, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Boot-fatal classes where the dedi stays alive but never hosts. Note
    /// "CHostState::FrameUpdate: Shutdown host game" is NOT here: it prints on
    /// every boot (empty-game teardown ~t9s) and on changelevel.
    /// </summary>
    public static bool IsFatalLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        var s = HostedConsoleTap.StripAnsi(line);
        return s.Contains("Level not valid", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Unable to find level", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Host_Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Uncaught Squirrel error on the dedi. The host then schedules
    /// HS_GAME_SHUTDOWN and the client loses its connection.
    /// </summary>
    public static bool IsScriptErrorLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        var s = HostedConsoleTap.StripAnsi(line);
        return s.Contains(ScriptErrorTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Body after SCRIPT ERROR:, minus the [SERVER] banner. Null when the
    /// line is not a script error. Truncated for a dialog.
    /// </summary>
    public static string? ScriptErrorExcerpt(string? line, int maxLen = 280)
    {
        if (string.IsNullOrEmpty(line) || maxLen < 8)
            return null;

        var s = HostedConsoleTap.StripAnsi(line).Trim();
        var at = s.IndexOf(ScriptErrorTag, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        s = s[(at + ScriptErrorTag.Length)..].Trim();
        const string serverBanner = "[SERVER]";
        if (s.StartsWith(serverBanner, StringComparison.OrdinalIgnoreCase))
            s = s[serverBanner.Length..].Trim();
        if (s.Length == 0)
            return null;
        if (s.Length > maxLen)
            s = s[..maxLen] + "...";
        return s;
    }

    public static bool IsSafeEventName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!name.StartsWith(EventPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = name.AsSpan(EventPrefix.Length);
        if (rest.Length is < 1 or > 64)
            return false;
        foreach (var c in rest)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')
                continue;
            return false;
        }
        return true;
    }

    public static string? SelfCheck()
    {
        if (!IsSafeEventName(EventPrefix + "abc12"))
            return "safe name rejected";
        if (IsSafeEventName(@"Global\r5f-host-abc"))
            return "global namespace accepted";
        if (IsSafeEventName(EventPrefix + "bad name"))
            return "space accepted";
        if (IsSafeEventName(EventPrefix))
            return "empty suffix accepted";
        if (!IsReadyLine("Native(E): [R5F-HOST] ready mp_rr_x"))
            return "ready tag missed";
        if (!IsReadyLine("Native(S): [s21-dedi] ODL precache replay: 1 engine"))
            return "fallback tag missed";
        if (IsReadyLine("[R5F-HOST] ready mp_lobby", "mp_rr_x"))
            return "map filter missed";
        if (!IsReadyLine("[R5F-HOST] ready mp_rr_x", "mp_rr_x"))
            return "map filter rejected match";
        if (!IsMapReadyLine("Native(E): [R5F-HOST] ready mp_rr_arena_composite", "mp_rr_arena_composite"))
            return "map ready missed";
        if (IsMapReadyLine("[R5F-HOST] ready mp_lobby", "mp_rr_x"))
            return "map ready accepted mismatch";
        if (IsMapReadyLine("[s21-dedi] ODL precache replay: 1 engine", "mp_rr_x"))
            return "fallback treated as map ready";
        if (!IsFatalLine("CHostState::State_NewGame: Level not valid"))
            return "fatal missed";
        if (IsFatalLine("Loading level: 'mp_rr_x'"))
            return "load start treated fatal";
        if (IsFatalLine("Native(E):CHostState::FrameUpdate: Shutdown host game"))
            return "boot shutdown treated fatal";
        if (IsFatalLine("Script(S):SCRIPT ERROR: [SERVER] missing attachment blade_base"))
            return "script error treated fatal";
        if (!IsScriptErrorLine("Native(S): SCRIPT ERROR: [SERVER] foo"))
            return "script error tag missed";
        if (IsScriptErrorLine(" -> weapon.PlayWeaponEffect( GH_SWORD_IDLE_FX_1P"))
            return "stack frame treated as script error";
        var excerpt = ScriptErrorExcerpt(
            "Script(S):SCRIPT ERROR: [SERVER] PlayWeaponParticleEffect: missing attachment blade_base");
        if (excerpt != "PlayWeaponParticleEffect: missing attachment blade_base")
            return "script excerpt missed";
        if (ScriptErrorExcerpt("Loading level: 'mp_rr_x'") is not null)
            return "excerpt from non-error";
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _event.Dispose(); }
        catch { /* ignore */ }
    }
}
