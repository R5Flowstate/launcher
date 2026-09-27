using System.Windows;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    LauncherHandoffListener? _handoff;
    bool _handoffBusy;

    IReadOnlyDictionary<string, string> HandoffEnvironment()
    {
        if (_handoff is null)
        {
            _handoff = LauncherHandoffListener.Start();
            _handoff.RequestReceived += req => Dispatcher.BeginInvoke(() => _ = HandleModJoinHandoffAsync(req));
        }

        return _handoff.ToEnvironment();
    }

    void DisposeHandoff()
    {
        _handoff?.Dispose();
        _handoff = null;
    }

    /// <summary>
    /// The game could not join a server for missing mods. The requirements come
    /// from the master listing, never from the game, and the join flow asks the
    /// player before anything is downloaded.
    /// </summary>
    async Task HandleModJoinHandoffAsync(HandoffRequest req)
    {
        if (_handoffBusy || _joinBusy || _quickPlayBusy || _installBusy)
            return;

        _handoffBusy = true;
        try
        {
            App.BringToFront();
            Log($"Handoff: game asked to install mods for {req.Host}:{req.Port}");

            await RefreshServerListAsync(quiet: true).ConfigureAwait(true);
            var matches = _serverRows
                .Select(r => r.Listing)
                .Where(l => string.Equals(l.Ip, req.Host, StringComparison.OrdinalIgnoreCase)
                            && (req.Port == 0 || l.Port == req.Port))
                .ToList();

            if (matches.Count != 1)
            {
                MessageBox.Show(
                    this,
                    Loc.Get("mods_handoff_unlisted"),
                    Loc.Get("mods_join_missing_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // The request only exists to install missing mods. Without any, it would
            // just be a script-chosen server switch that kills the running game.
            var root = ModsInstallRoot();
            var enabled = new HashSet<string>(ModsStore.EnabledIds(root), StringComparer.OrdinalIgnoreCase);
            if (DistinctIds(matches[0].RequiredMods).All(enabled.Contains))
            {
                MessageBox.Show(
                    this,
                    Loc.Format("mods_handoff_nothing", matches[0].Name),
                    Loc.Get("mods_join_missing_title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await RunJoinAsync(matches[0], forceRelaunch: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("Handoff failed: " + ex.Message);
            SetBrowserStatus(Loc.Format("mods_install_failed", ex.Message));
        }
        finally
        {
            _handoffBusy = false;
        }
    }
}
