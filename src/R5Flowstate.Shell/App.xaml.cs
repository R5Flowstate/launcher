using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace R5Flowstate.Shell;

public partial class App : Application
{
    /// <summary>Optional --install-root from CLI (session override; not auto-persisted).</summary>
    public string? CliInstallRoot { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Surface silent WPF / UI-thread deaths (otherwise the window never appears).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        CliInstallRoot = ParseInstallRoot(e.Args);
        var settings = SettingsStore.Load();
        // Hardware rendering by default; ForceSoftwareRender in settings.json is
        // the escape hatch for machines whose GPU drivers misrender WPF.
        if (settings.ForceSoftwareRender)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        Loc.Initialize(NoticeLanguages.ResolveUi(settings.UiLanguage, settings.EulaLanguage));
        JoinLink.RegisterProtocol();
        SingleInstance.Watch(() => Dispatcher.BeginInvoke(() =>
        {
            BringToFront();
            (Current?.MainWindow as MainWindow)?.ConsumePendingJoin();
        }));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Release();
        base.OnExit(e);
    }

    internal static void BringToFront()
    {
        if (Current?.MainWindow is not Window w)
            return;
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
    }

    private bool _exceptionDialogOpen;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_exceptionDialogOpen)
            return;
        _exceptionDialogOpen = true;
        try
        {
            MessageBox.Show(
                Loc.Format("msg_error_prefix", e.Exception),
                Loc.Get("title_app"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _exceptionDialogOpen = false;
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            MessageBox.Show(
                Loc.Format("msg_fatal_prefix", e.ExceptionObject),
                Loc.Get("title_app"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // ignore
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        try
        {
            MessageBox.Show(
                Loc.Format("msg_bg_error_prefix", e.Exception.GetBaseException()),
                Loc.Get("title_app"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // ignore
        }
    }

    private static string? ParseInstallRoot(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--install-root" or "-install-root")
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1].Trim();
            }
            else if (a.StartsWith("--install-root=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a["--install-root=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
        }

        return null;
    }
}
