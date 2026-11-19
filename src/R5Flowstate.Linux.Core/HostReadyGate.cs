using System.Diagnostics;
using System.Threading;

namespace R5Flowstate.Linux.Core;

public enum HostReadyWait
{
    Ready,
    ProcessDied,
    Fatal,
    Cancelled,
}

/// <summary>
/// Local-play rendezvous: the launcher waits until the level it asked for is
/// actually live before it counts the boot as good. Ported from the Windows
/// Shell's <c>HostReadyGate</c>.
///
/// What changed, and why it had to. On Windows the dedi signals a *named kernel
/// event* (<c>R5F_HOST_READY</c> = <c>Local\r5f-host-&lt;guid&gt;</c>) and the
/// gate waits on that handle with no timeout. A named event created inside a
/// Wine prefix is not something a native Linux process can open, so that
/// rendezvous cannot exist here — and it does not need to: the gate is fed the
/// dedi's own console lines by <see cref="ConsoleTap"/>, and upstream's
/// doc comment already calls the tagged line "a fallback for older dedi". On
/// Linux the tagged line *is* the mechanism, so the wait is a
/// <see cref="ManualResetEventSlim"/> this process owns and the signal comes
/// from the line reader. Every classifier below is upstream's, character for
/// character, because they are the contract with the dedi's own output and the
/// Windows launcher has to keep reading that output the same way.
///
/// Dropped with the event: <c>EnvName</c>, <c>EventPrefix</c> and
/// <c>IsSafeEventName</c> (the child is no longer handed an event name to
/// signal, so there is nothing to validate). Nothing reads them, and keeping a
/// validator for a channel that does not exist would only invite a caller to
/// think the named event works here.
/// </summary>
public sealed class HostReadyGate : IDisposable
{
    public const string ReadyTag = "[R5F-HOST] ready";
    public const string FallbackTag = "[s21-dedi] ODL precache replay";
    public const string ScriptErrorTag = "SCRIPT ERROR:";
    public const string ClientErrorDialogTag = "UICodeCallback_ErrorDialog:";

    readonly ManualResetEventSlim _signal = new(false);
    int _signaled;
    int _fatal;
    int _disposed;

    /// <summary>The tagged ready line arrived (see <see cref="IsReadyLine"/>).</summary>
    public void SignalFromConsole()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        Interlocked.Exchange(ref _signaled, 1);
        try { _signal.Set(); }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    /// <summary>The boot cannot succeed: map invalid, host error, or an uncaught
    /// script error that has since torn the host down.</summary>
    public void SignalFatal()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        Interlocked.Exchange(ref _fatal, 1);
        try { _signal.Set(); }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    /// <summary>
    /// Wait has no timeout — process death, a fatal classification and
    /// cancellation are the only exits, exactly as upstream. A dedi that never
    /// becomes ready therefore keeps the caller waiting rather than being
    /// declared ready by a clock, which is what the tagged line is for.
    /// </summary>
    public HostReadyWait Wait(Process? dedi, CancellationToken ct)
    {
        while (true)
        {
            if (Volatile.Read(ref _fatal) != 0)
                return HostReadyWait.Fatal;
            if (Volatile.Read(ref _signaled) != 0)
                return HostReadyWait.Ready;
            if (ct.IsCancellationRequested)
                return HostReadyWait.Cancelled;

            if (dedi is not null)
            {
                try
                {
                    if (dedi.HasExited)
                        return HostReadyWait.ProcessDied;
                }
                catch (InvalidOperationException)
                {
                    return HostReadyWait.ProcessDied;
                }
            }

            try
            {
                if (_signal.Wait(250, ct))
                    continue; // re-read the flags at the top
            }
            catch (OperationCanceledException)
            {
                return HostReadyWait.Cancelled;
            }
            catch (ObjectDisposedException)
            {
                return HostReadyWait.Cancelled;
            }
        }
    }

    /// <summary>
    /// The boot-reached-world line, optionally for one map only. The
    /// ODL-replay fallback has no map name in it, so with
    /// <paramref name="wantMap"/> set only the tagged form can satisfy it.
    /// </summary>
    public static bool IsReadyLine(string? line, string? wantMap = null)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        var s = ConsoleTap.StripAnsi(line);
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
    /// Changelevel complete for this stem. The tagged ready line only — the
    /// ODL-replay fallback has no map name, so it cannot tell map A from map B.
    /// </summary>
    public static bool IsMapReadyLine(string? line, string? map)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(map))
            return false;

        var s = ConsoleTap.StripAnsi(line);
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
        var s = ConsoleTap.StripAnsi(line);
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
        var s = ConsoleTap.StripAnsi(line);
        return s.Contains(ScriptErrorTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Host teardown. Prints on every boot and changelevel too, so it only
    /// means "the match is gone" when it follows a script error.
    /// </summary>
    public static bool IsHostShutdownLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        var s = ConsoleTap.StripAnsi(line);
        return s.Contains("Shutdown host game", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A level load starts; the frame loop legitimately stalls for a while.</summary>
    public static bool IsLevelLoadLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        var s = ConsoleTap.StripAnsi(line);
        return s.Contains("Loading level:", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Level loaded:", StringComparison.OrdinalIgnoreCase)
            || s.Contains("changelevel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The client's own disconnect dialog text ("Connection to server timed
    /// out ..."). Null for any other line.
    /// </summary>
    public static string? ClientErrorDialogText(string? line, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(line))
            return null;
        var s = ConsoleTap.StripAnsi(line);
        var at = s.IndexOf(ClientErrorDialogTag, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        s = s[(at + ClientErrorDialogTag.Length)..].Trim();
        var see = s.IndexOf("See ea.com", StringComparison.OrdinalIgnoreCase);
        if (see > 0)
            s = s[..see].Trim();
        if (s.Length == 0)
            return null;
        if (s.Length > maxLen)
            s = s[..maxLen] + "...";
        return s;
    }

    /// <summary>
    /// Body after SCRIPT ERROR:, minus the [SERVER] banner. Null when the
    /// line is not a script error. Truncated for a dialog.
    /// </summary>
    public static string? ScriptErrorExcerpt(string? line, int maxLen = 280)
    {
        if (string.IsNullOrEmpty(line) || maxLen < 8)
            return null;

        var s = ConsoleTap.StripAnsi(line).Trim();
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _signal.Set(); }
        catch { /* ignore */ }
        try { _signal.Dispose(); }
        catch { /* ignore */ }
    }
}
