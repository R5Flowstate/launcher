using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    const int WmGetMinMaxInfo = 0x0024;
    const uint MonitorDefaultToNearest = 2;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo && TryClampMaximizedToWorkArea(hwnd, lParam))
            handled = true;
        return IntPtr.Zero;
    }

    // WindowChrome + WindowStyle=None maximizes onto the full monitor.
    // Clamp to the work area so the footer stays above the taskbar.
    bool TryClampMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.rcWork;
        var screen = info.rcMonitor;
        mmi.ptMaxPosition.X = Math.Abs(work.Left - screen.Left);
        mmi.ptMaxPosition.Y = Math.Abs(work.Top - screen.Top);
        mmi.ptMaxSize.X = Math.Abs(work.Right - work.Left);
        mmi.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct })
        {
            if (MinWidth > 0)
                mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * ct.TransformToDevice.M11);
            if (MinHeight > 0)
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * ct.TransformToDevice.M22);
        }

        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
        return true;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    struct PointI
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo
    {
        public PointI ptReserved;
        public PointI ptMaxSize;
        public PointI ptMaxPosition;
        public PointI ptMinTrackSize;
        public PointI ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RectI
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MonitorInfo
    {
        public int cbSize;
        public RectI rcMonitor;
        public RectI rcWork;
        public int dwFlags;
    }
}
