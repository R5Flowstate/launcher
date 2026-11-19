using System.IO;
using System.Windows;
using System.Windows.Threading;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    // IShellLinkW::SetArguments truncates past INFOTIPSIZE.
    const int ShortcutArgsMax = 1023;

    bool _shortcutOfferQueued;
    bool _shortcutsReconciled;
    bool _suppressShortcutEvent;
    readonly Dictionary<string, string> _shortcutWritten = new(StringComparer.OrdinalIgnoreCase);

    GameBuild ActiveBuild => BuildOf(InstallRoot());

    string GameShortcutPath() => DesktopShortcuts.GamePath(ActiveBuild);

    /// <summary>The active folder for its build, else the last folder remembered for that build.</summary>
    string? RootForBuild(GameBuild build)
    {
        var active = InstallRoot();
        if (!string.IsNullOrWhiteSpace(active) && GameBuilds.Detect(active) == build)
            return active;
        var kept = build == GameBuild.S30 ? _settings.S30InstallPath : _settings.S21InstallPath;
        return IsBuildFolder(kept, build) ? kept : null;
    }

    /// <summary>
    /// The installer names the launcher shortcut "R5Flowstate"; next to the game shortcut it
    /// becomes "R5Flowstate Launcher". Only a link that targets the launcher exe is touched.
    /// </summary>
    void RenameLauncherShortcut()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var oldPath = Path.Combine(desktop, "R5Flowstate.lnk");
            if (!File.Exists(oldPath))
                return;
            if (!string.Equals(Path.GetFileName(DesktopShortcuts.ReadTarget(oldPath)), "R5FlowstateLauncher.exe",
                    StringComparison.OrdinalIgnoreCase))
                return;

            var newPath = Path.Combine(desktop, DesktopShortcuts.LauncherShortcutName);
            if (File.Exists(newPath))
                File.Delete(oldPath);
            else
                File.Move(oldPath, newPath);
            Log("Desktop launcher shortcut renamed to \"R5Flowstate Launcher\".");
        }
        catch (Exception ex)
        {
            Log($"Launcher shortcut rename failed: {ex.Message}");
        }
    }

    void MaybeOfferDesktopShortcut()
    {
        ReconcileShortcutsOnce();
        if (_shortcutOfferQueued || !CanOfferDesktopShortcut())
            return;

        _shortcutOfferQueued = true;
        Dispatcher.BeginInvoke(ShowDesktopShortcutOffer, DispatcherPriority.ApplicationIdle);
    }

    bool ShortcutOffered(GameBuild build) =>
        build == GameBuild.S30 ? _settings.S30ShortcutOffered : _settings.DesktopShortcutOffered;

    bool CanOfferDesktopShortcut()
    {
        var build = ActiveBuild;
        if (!IsLoaded || _installBusy || _verifyBusy || ShortcutOffered(build))
            return false;
        if (PanelSimpleSetup?.Visibility == Visibility.Visible)
            return false;
        // One modal at a time; the HD offer goes first and this retries on the next refresh.
        if (_hdAnnounceQueued || CanOfferHdTextures())
            return false;
        if (File.Exists(DesktopShortcuts.GamePath(build)))
        {
            MarkShortcutOffered(build);
            return false;
        }

        var install = InstallRoot();
        return !string.IsNullOrWhiteSpace(install) && HealthForUi(install).IsReady
            && GameShortcutTarget(install) is not null;
    }

    void ShowDesktopShortcutOffer()
    {
        try
        {
            if (!CanOfferDesktopShortcut())
                return;

            var build = ActiveBuild;
            MarkShortcutOffered(build);
            var ok = MessageBox.Show(
                this,
                Loc.Get(build == GameBuild.S30 ? "desktop_shortcut_offer_s30" : "desktop_shortcut_offer"),
                Loc.Get("desktop_shortcut"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ok == MessageBoxResult.Yes)
                WriteShortcuts(build, force: true);
            SyncShortcutCheckbox();
        }
        finally
        {
            _shortcutOfferQueued = false;
        }
    }

    void MarkShortcutOffered(GameBuild build)
    {
        if (build == GameBuild.S30)
            _settings.S30ShortcutOffered = true;
        else
            _settings.DesktopShortcutOffered = true;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }

    string? GameShortcutTarget(string install)
    {
        var root = Path.GetFullPath(install);
        var dx12 = Path.Combine(root, ProcessSpawner.ClientDx12ExeName);
        if ((_settings.UseDx12 || GameBuilds.IsS30(root)) && File.Exists(dx12))
            return dx12;
        var dx11 = Path.Combine(root, ProcessSpawner.ClientExeName);
        return File.Exists(dx11) ? dx11 : null;
    }

    /// <summary>Runs once the UI can build launch args: fixes a misdirected link and catches moved folders.</summary>
    void ReconcileShortcutsOnce()
    {
        if (_shortcutsReconciled || !IsLoaded)
            return;
        _shortcutsReconciled = true;
        RepairMisdirectedS21Shortcut();
        RefreshDesktopShortcut();
    }

    /// <summary>The S21 link must never point into an S30 folder; both builds get their own link back.</summary>
    void RepairMisdirectedS21Shortcut()
    {
        var path = DesktopShortcuts.GamePath(GameBuild.S21);
        try
        {
            if (!File.Exists(path) ||
                !GameBuilds.IsS30(Path.GetDirectoryName(DesktopShortcuts.ReadTarget(path))))
                return;

            File.Delete(path);
            Log("Desktop shortcut pointed the S21 link at an S30 folder; rewriting both.");
            if (RootForBuild(GameBuild.S21) is not null)
                WriteShortcuts(GameBuild.S21, force: true);
            if (RootForBuild(GameBuild.S30) is not null)
            {
                WriteShortcuts(GameBuild.S30, force: true);
                MarkShortcutOffered(GameBuild.S30);
            }
            SyncShortcutCheckbox();
        }
        catch (Exception ex)
        {
            Log($"Desktop shortcut repair failed: {ex.Message}");
        }
    }

    /// <summary>Keeps existing game shortcuts in step with the launcher's renderer and launch options.</summary>
    void RefreshDesktopShortcut()
    {
        if (!IsLoaded)
            return;
        foreach (var build in Enum.GetValues<GameBuild>())
        {
            if (File.Exists(DesktopShortcuts.GamePath(build)) && RootForBuild(build) is not null)
                WriteShortcuts(build, force: false);
        }
    }

    /// <summary>Writes a build's links. Refreshes only update a launcher link that still exists.</summary>
    bool WriteShortcuts(GameBuild build, bool force)
    {
        try
        {
            var install = RootForBuild(build);
            if (string.IsNullOrWhiteSpace(install))
                return false;
            var target = GameShortcutTarget(install);
            if (target is null)
            {
                Log("Desktop shortcut skipped: game executable not found.");
                return false;
            }

            var args = LaunchArgs.FormatArgumentsOnly(
                WithGameBuildArgs(LaunchRole.Client, install, BuildClientArgs(includeConnect: false)));
            if (args.Length > ShortcutArgsMax)
            {
                Log($"Desktop shortcut skipped: launch options are {args.Length} characters (limit {ShortcutArgsMax}).");
                return false;
            }

            var path = DesktopShortcuts.GamePath(build);
            var icon = Path.Combine(AppContext.BaseDirectory, DesktopShortcuts.GameIcon(build));
            var stamp = target + "\n" + args + "\n" + icon;
            if (force || !_shortcutWritten.TryGetValue(path, out var written) || written != stamp)
            {
                DesktopShortcuts.Write(path, target, args, Path.GetDirectoryName(target) ?? install,
                    build == GameBuild.S30 ? "R5Flowstate S30" : "R5Flowstate", icon);
                _shortcutWritten[path] = stamp;
                Log($"Desktop shortcut written: {Path.GetFileName(path)} -> {Path.GetFileName(target)}");
            }

            if (build == GameBuild.S30 && (force || File.Exists(DesktopShortcuts.S30LauncherPath)))
                WriteS30LauncherShortcut(force);
            RenameLauncherShortcut();
            return true;
        }
        catch (Exception ex)
        {
            Log($"Desktop shortcut failed: {ex.Message}");
            return false;
        }
    }

    void WriteS30LauncherShortcut(bool force)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;

        var path = DesktopShortcuts.S30LauncherPath;
        if (!force && _shortcutWritten.TryGetValue(path, out var written) && written == exe)
            return;

        DesktopShortcuts.Write(path, exe, GameBuildLink.Argument(GameBuild.S30),
            Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory, TaskbarIdentity.S30DisplayName,
            Path.Combine(AppContext.BaseDirectory, DesktopShortcuts.S30LauncherIcon), TaskbarIdentity.S30AppId);
        _shortcutWritten[path] = exe;
        Log($"Desktop shortcut written: {Path.GetFileName(path)}");
    }

    void RemoveBuildShortcuts(GameBuild build)
    {
        var paths = build == GameBuild.S30
            ? new[] { DesktopShortcuts.GamePath(build), DesktopShortcuts.S30LauncherPath }
            : new[] { DesktopShortcuts.GamePath(build) };
        foreach (var path in paths)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log($"Desktop shortcut remove failed: {ex.Message}"); }
            _shortcutWritten.Remove(path);
        }
    }

    void SyncShortcutCheckbox()
    {
        if (ChkDesktopShortcut is null)
            return;
        _suppressShortcutEvent = true;
        try { ChkDesktopShortcut.IsChecked = File.Exists(GameShortcutPath()); }
        finally { _suppressShortcutEvent = false; }
    }

    private void OnDesktopShortcutChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressShortcutEvent)
            return;

        var build = ActiveBuild;
        MarkShortcutOffered(build);
        if (ChkDesktopShortcut.IsChecked == true)
            WriteShortcuts(build, force: true);
        else
            RemoveBuildShortcuts(build);
        SyncShortcutCheckbox();
    }
}
