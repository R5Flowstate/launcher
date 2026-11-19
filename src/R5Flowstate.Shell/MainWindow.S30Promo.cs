using System.IO;
using System.Windows;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>
/// Until R5F 2.0 is installed the launcher opens on its install screen, and offers to remove the Season 21
/// folder once the new install has finished.
/// </summary>
public partial class MainWindow
{
    string? _s21SizePath;
    long _s21SizeBytes = -1;

    /// <summary>Runs before the first paint; an explicit --build request wins.</summary>
    void PromoteS30AtStartup(bool buildRequested)
    {
        if (buildRequested || GameBuilds.IsS30(_settings.InstallPath) || IsBuildFolder(_settings.S30InstallPath, GameBuild.S30))
            return;

        var current = _settings.InstallPath?.Trim() ?? string.Empty;
        _settings.SetupBuild = GameBuild.S30;
        if (!IsBuildFolder(current, GameBuild.S21))
            return;

        _settings.S21InstallPath = current;
        _settings.InstallPath = NewFolderFor(GameBuild.S30, current);
        // The stranded-folder guard would otherwise send the empty R5F 2.0 folder straight back to Season 21.
        _settings.PreviousInstallPath = string.Empty;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }

    string? ReplaceableS21Folder()
    {
        var s21 = _settings.S21InstallPath?.Trim();
        if (!IsBuildFolder(s21, GameBuild.S21))
            return null;
        var root = ReadInstallPathBox().Trim();
        return string.Equals(Path.GetFullPath(s21!), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? null : s21;
    }

    void RefreshReplaceS21Option()
    {
        if (ChkRemoveS21After is null)
            return;
        var s21 = ActiveBuild == GameBuild.S30 && _simplePlayKind != SimplePlayKind.Repair ? ReplaceableS21Folder() : null;
        ChkRemoveS21After.Visibility = s21 is null ? Visibility.Collapsed : Visibility.Visible;
        if (s21 is null)
            return;

        TxtRemoveS21After.Text = Loc.Format("remove_s21_after",
            _s21SizeBytes > 0 && s21 == _s21SizePath ? FormatBytes(_s21SizeBytes) : Loc.Get("uninstall_size_unknown"));
        if (s21 == _s21SizePath)
            return;

        _s21SizePath = s21;
        _s21SizeBytes = -1;
        _ = Task.Run(() => FolderBytes(s21)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && _s21SizePath == s21)
            {
                _s21SizeBytes = t.Result;
                RefreshReplaceS21Option();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static long FolderBytes(string root)
    {
        long total = 0;
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            total += f.Length;
        return total;
    }

    /// <summary>After a finished R5F 2.0 install: deletes the Season 21 folder when the player asked for it.</summary>
    async Task RemoveReplacedS21Async(string s30Root)
    {
        var s21 = _settings.S21InstallPath?.Trim();
        if (!GameBuilds.IsS30(s30Root) || !IsBuildFolder(s21, GameBuild.S21)
            || string.Equals(Path.GetFullPath(s21!), Path.GetFullPath(s30Root), StringComparison.OrdinalIgnoreCase))
            return;
        if (ProcessSpawner.IsRoleAlive(LaunchRole.Client, s21!) || ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, s21!))
        {
            Log("Season 21 is running; its folder was kept.");
            return;
        }

        Log($"Removing Season 21 at {s21} after the R5F 2.0 install.");
        SetSimpleStatus(Loc.Get("status_removing"));
        RemoveReport report;
        try
        {
            report = await Task.Run(() => DirectoryRemover.Remove(s21!)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("Season 21 removal failed: " + ex.Message);
            SetSimpleStatus(Loc.Get("status_removed_partial"));
            return;
        }

        Log($"Season 21 removed: {report.FilesDeleted} file(s), {report.BytesDeleted / (1024 * 1024)} MB, " +
            $"{report.Failures.Count} failure(s).");
        if (report.RootRemoved)
        {
            _settings.S21InstallPath = string.Empty;
            RemoveBuildShortcuts(GameBuild.S21);
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        }
        SetSimpleStatus(Loc.Get(report.RootRemoved && report.Failures.Count == 0 ? "ready_to_play" : "status_removed_partial"));
        PaintBuildUi(ActiveBuild);
    }
}
