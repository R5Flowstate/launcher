using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

/// <summary>
/// One shell per logon session. A second start raises the live window
/// instead of becoming a second owner of the same game processes.
/// </summary>
static class SingleInstance
{
    const string MutexName = @"Local\" + ProductConstants.SingleInstanceMutexName;
    const string ActivateName = @"Local\" + ProductConstants.SingleInstanceMutexName + ".Activate";
    const int ActivateTries = 15;
    const int ActivateWaitMs = 40;

    static readonly object s_gate = new();
    static Mutex? s_mutex;
    static EventWaitHandle? s_activate;
    static Thread? s_watch;
    static Action? s_onActivate;
    static bool s_pendingActivate;

    public static bool Claim()
    {
        s_mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created)
        {
            try
            {
                if (!s_mutex.WaitOne(0))
                {
                    Release();
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died; this process takes the slot.
            }
        }

        try
        {
            s_activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        }
        catch
        {
            s_activate = null;
        }

        s_watch = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "r5f-single-instance",
        };
        s_watch.Start();
        return true;
    }

    public static void Watch(Action onActivate)
    {
        bool fire;
        lock (s_gate)
        {
            s_onActivate = onActivate;
            fire = s_pendingActivate;
            s_pendingActivate = false;
        }
        if (fire)
            onActivate();
    }

    public static void ActivateExisting()
    {
        for (var i = 0; i < ActivateTries; i++)
        {
            SignalActivate();
            if (TryRaiseOtherWindow())
                return;
            Thread.Sleep(ActivateWaitMs);
        }
    }

    public static void Release()
    {
        lock (s_gate)
            s_onActivate = null;

        var ev = s_activate;
        s_activate = null;
        try { ev?.Set(); } catch { }
        try { ev?.Dispose(); } catch { }

        var mx = s_mutex;
        s_mutex = null;
        if (mx is null)
            return;
        try { mx.ReleaseMutex(); } catch { }
        try { mx.Dispose(); } catch { }
    }

    static void WatchLoop()
    {
        while (true)
        {
            var ev = s_activate;
            if (ev is null)
                return;
            try
            {
                ev.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                return;
            }

            if (s_activate is null)
                return;
            RaiseActivate();
        }
    }

    static void RaiseActivate()
    {
        Action? cb;
        lock (s_gate)
        {
            cb = s_onActivate;
            if (cb is null)
            {
                s_pendingActivate = true;
                return;
            }
        }

        try { cb(); }
        catch { }
    }

    static void SignalActivate()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ActivateName);
            ev.Set();
        }
        catch
        {
            // First instance is mid-start or already gone.
        }
    }

    static bool TryRaiseOtherWindow()
    {
        var me = Environment.ProcessId;
        var names = new[]
        {
            Process.GetCurrentProcess().ProcessName,
            Path.GetFileNameWithoutExtension(ProductConstants.ShellExeName),
        };

        var raised = false;
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == me)
                        continue;
                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                        hwnd = FindTopWindow(p.Id);
                    if (hwnd == IntPtr.Zero)
                        continue;

                    AllowSetForegroundWindow(p.Id);
                    if (IsIconic(hwnd))
                        ShowWindow(hwnd, SwRestore);
                    ShowWindow(hwnd, SwShow);
                    SetForegroundWindow(hwnd);
                    raised = true;
                }
                catch
                {
                    // access denied / exited
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        return raised;
    }

    static IntPtr FindTopWindow(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid != (uint)pid)
                return true;
            if (!IsWindowVisible(h))
                return true;
            if (GetWindow(h, GwOwner) != IntPtr.Zero)
                return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    const int SwRestore = 9;
    const int SwShow = 5;
    const uint GwOwner = 4;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);
}
