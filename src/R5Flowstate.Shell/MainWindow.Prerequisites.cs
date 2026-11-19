using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    static readonly TimeSpan PrereqInstallTimeout = TimeSpan.FromMinutes(10);

    bool _prereqAsked;

    /// <summary>
    /// Offers the missing Windows runtimes once per session before the client starts.
    /// Never blocks the launch; a declined or failed install is only logged and reported.
    /// </summary>
    private void EnsureClientPrerequisites(bool quiet)
    {
        var missing = Prerequisites.Missing();
        if (missing.Count == 0)
            return;

        var list = string.Join(Environment.NewLine, missing);
        Log($"prerequisites missing: {string.Join("; ", missing)}");
        if (quiet || _prereqAsked)
            return;
        _prereqAsked = true;

        var answer = MessageBox.Show(this, Loc.Format("prereq_missing", list), Loc.Get("prereq_title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            Log("prerequisites: install declined");
            return;
        }

        UpdateStatus(Loc.Get("prereq_installing"));
        var wasEnabled = IsEnabled;
        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        string? error;
        try
        {
            var task = Task.Run(() => Prerequisites.InstallMissing(PrereqInstallTimeout));
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            error = task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = wasEnabled;
        }

        if (error is null)
        {
            Log("prerequisites: installed");
            UpdateStatus(Loc.Get("prereq_done"));
            return;
        }

        Log($"prerequisites: install failed: {error}");
        MessageBox.Show(this, Loc.Format("prereq_failed", error), Loc.Get("prereq_title"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
