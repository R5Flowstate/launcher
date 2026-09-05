using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using Velopack;

namespace R5Flowstate.Shell;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Hooks (--veloapp-install/updated/obsolete/uninstall) must run even
        // when Update.exe is not beside this exe (mid-extract). Run() exits
        // on a recognised hook; if it returns we still must not open WPF.
        var hook = IsVelopackHook(args);
        if (hook || HasUpdateExe())
        {
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ => UninstallHook.Run())
                .Run();
        }
        if (hook)
            return;

        if (JoinLink.TryParse(args, out var joinKey))
            JoinLink.StashPending(joinKey);

        if (!SingleInstance.Claim())
        {
            SingleInstance.ActivateExisting();
            return;
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    static bool IsVelopackHook(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool HasUpdateExe()
    {
        var dir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(dir, "Update.exe")))
            return true;

        var parent = Path.GetFullPath(Path.Combine(dir, "..", "Update.exe"));
        return File.Exists(parent);
    }
}
