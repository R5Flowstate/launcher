using System.IO;
using System.Runtime.InteropServices;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

/// <summary>
/// Windows draws a pinned app's taskbar button from the pinned link, not the window icon. On S30.1 the window
/// takes its own app id and relaunch details, so it gets a blue button and pins back into S30.1. S21 keeps the
/// default identity. Window properties must be cleared before the window is destroyed.
/// </summary>
static class TaskbarIdentity
{
    public const string S30AppId = "R5Flowstate.Launcher.S30";
    public const string S30DisplayName = "R5Flowstate S30 Launcher";

    public static void Apply(IntPtr hwnd, GameBuild build)
    {
        var exe = Environment.ProcessPath;
        if (build == GameBuild.S30 && !string.IsNullOrEmpty(exe))
        {
            var icon = Path.Combine(AppContext.BaseDirectory, DesktopShortcuts.S30LauncherIcon);
            Set(hwnd, S30AppId, $"\"{exe}\" {GameBuildLink.Argument(GameBuild.S30)}", S30DisplayName, $"{icon},0");
        }
        else
        {
            Clear(hwnd);
        }
    }

    public static void Clear(IntPtr hwnd) => Set(hwnd, null, null, null, null);

    static void Set(IntPtr hwnd, string? appId, string? command, string? name, string? icon)
    {
        if (hwnd == IntPtr.Zero)
            return;

        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
            ShellProperties.SetString(store, ShellProperties.RelaunchCommand, command);
            ShellProperties.SetString(store, ShellProperties.RelaunchDisplayName, name);
            ShellProperties.SetString(store, ShellProperties.RelaunchIcon, icon);
            ShellProperties.SetString(store, ShellProperties.AppUserModelId, appId);
            store.Commit();
        }
        catch
        {
            // The window just keeps the default taskbar identity.
        }
        finally
        {
            if (store is not null)
                Marshal.FinalReleaseComObject(store);
        }
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}

static class ShellProperties
{
    static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static readonly PropertyKey RelaunchCommand = new(AppUserModelFormat, 2);
    public static readonly PropertyKey RelaunchIcon = new(AppUserModelFormat, 3);
    public static readonly PropertyKey RelaunchDisplayName = new(AppUserModelFormat, 4);
    public static readonly PropertyKey AppUserModelId = new(AppUserModelFormat, 5);

    const ushort VtLpwstr = 31;

    /// <summary>A null value removes the property.</summary>
    public static void SetString(IPropertyStore store, PropertyKey key, string? value)
    {
        var variant = new PropVariant();
        if (value is not null)
        {
            variant.Type = VtLpwstr;
            variant.Pointer = Marshal.StringToCoTaskMemUni(value);
        }

        try
        {
            store.SetValue(ref key, ref variant);
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant.Pointer);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
readonly struct PropertyKey
{
    readonly Guid _format;
    readonly uint _id;

    public PropertyKey(Guid format, uint id)
    {
        _format = format;
        _id = id;
    }
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort Type;
    ushort _reserved1;
    ushort _reserved2;
    ushort _reserved3;
    public IntPtr Pointer;
    IntPtr _value2;
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}
