namespace R5Flowstate.Linux.Core;

using System.Diagnostics;

/// <summary>A Windows process the launcher started and is still holding on to.
///
/// The launcher used to wrap its launch helper in <c>using (process)</c>, which
/// disposed the process — and with it the redirected output readers — when the
/// fifteen-second grace window ended. For a client that is the whole run (the EA
/// App is meant to stay up), and for the EA installer on 2026-09-22 it meant
/// Wine's stderr stopped reaching launcher.log sixteen seconds into a six-minute
/// attempt: the MSI apply failed at 04:21:40 and left no account of itself,
/// because the pipe it would have written that to had been closed at 04:16:12.
///
/// So the readers have to outlive the helper. Whoever needs to know how the child
/// ended holds this, not a pid and not a boolean.
/// </summary>
public sealed class WindowsRun : IDisposable
{
    /// <summary>How many of a child's lines a launch keeps. Generous on purpose:
    /// the lines that diagnose a failure are not always the last ones — Wine's MSI
    /// halts and then the installer's embedded browser complains for another thirty
    /// lines — so a small ring is how a diagnosis gets evicted by its own noise.
    /// What gets *printed* is a shorter tail of this; what gets *classified* is
    /// all of it.</summary>
    public const int CaptureLimit = 200;

    readonly Process _process;
    readonly Func<IReadOnlyList<string>>? _tail;

    public WindowsRun(Process process, Func<IReadOnlyList<string>>? tail = null)
    {
        _process = process;
        _tail = tail;
    }

    /// <summary>Still running. A process object that cannot be asked — already
    /// disposed, or never started — counts as not running: every caller is asking
    /// "keep waiting?", and none of them wants an exception instead of an answer.</summary>
    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>The exit code, once there is one.</summary>
    public int? ExitCode
    {
        get
        {
            try { return _process.HasExited ? _process.ExitCode : null; }
            catch { return null; }
        }
    }

    /// <summary>The last lines it said, oldest first, capped at <paramref name="max"/>.
    /// Empty when it was started without capture — the honest answer, not a throw.</summary>
    public IReadOnlyList<string> Tail(int max = 40)
    {
        if (_tail is null || max <= 0)
            return Array.Empty<string>();

        try
        {
            var all = _tail();
            if (all.Count <= max)
                return all;

            var tail = new List<string>(max);
            for (var i = all.Count - max; i < all.Count; i++)
                tail.Add(all[i]);
            return tail;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        try { _process.Dispose(); } catch { }
    }
}
