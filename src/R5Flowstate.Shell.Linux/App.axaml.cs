using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using R5Flowstate.Shell.Linux.ViewModels;
using R5Flowstate.Shell.Linux.Views;

namespace R5Flowstate.Shell.Linux;

public partial class App : Application
{
    public override void Initialize()
    {
        // Loc tables must be resident before XAML bindings resolve.
        Loc.Initialize(NoticeLanguages.FromOs());
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // MainWindow builds its own DataContext (single VM, single EA check).
            desktop.MainWindow = new MainWindow();

            if (Program.SelfTest)
            {
                var window = desktop.MainWindow;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    int exit = SelfTestRunner.Run(window);
                    desktop.Shutdown(exit);
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}