using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

/// <summary>
/// The desktop links the launcher owns. Each game build has its own game link, so switching builds never
/// retargets the other build's link.
/// </summary>
static class DesktopShortcuts
{
    public const string LauncherShortcutName = "R5Flowstate Launcher.lnk";
    public const string S30LauncherIcon = "app_s30.ico";

    static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string GamePath(GameBuild build) => Path.Combine(Desktop,
        build == GameBuild.S30 ? "R5Flowstate S30 (DX12).lnk" : "R5Flowstate (DX12).lnk");

    public static string GameIcon(GameBuild build) => build == GameBuild.S30 ? "client_s30.ico" : "client.ico";

    public static string S30LauncherPath => Path.Combine(Desktop, "R5Flowstate S30 Launcher.lnk");

    public static void Write(string path, string target, string args, string workingDirectory,
        string description, string icon, string? appUserModelId = null)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            if (File.Exists(icon))
                link.SetIconLocation(icon, 0);
            if (appUserModelId is not null)
            {
                // Same id as the window, so a pinned link and the running launcher share one taskbar button.
                var store = (IPropertyStore)link;
                ShellProperties.SetString(store, ShellProperties.AppUserModelId, appUserModelId);
                store.Commit();
            }
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static string? ReadTarget(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Uninstall: every one of these points at, or takes its icon from, the launcher going away.</summary>
    public static void RemoveAll()
    {
        foreach (var path in new[]
                 {
                     GamePath(GameBuild.S21),
                     GamePath(GameBuild.S30),
                     S30LauncherPath,
                     Path.Combine(Desktop, LauncherShortcutName),
                 })
        {
            try { File.Delete(path); }
            catch { }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
