using System.Windows;
using System.Windows.Controls;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    enum HdPending
    {
        None,
        Enable,
        Disable,
    }

    bool _suppressHdEvent;

    // A toggle thrown mid-download is honoured when the run ends. Deleting or
    // fetching HD while the installer is writing the same folder races it.
    HdPending _hdPending = HdPending.None;

    string InstallRoot() => TxtInstallRoot?.Text?.Trim() ?? string.Empty;

    bool HdEnabledOnDisk(string install)
    {
        try
        {
            var p = System.IO.Path.Combine(install, ProductConstants.InstallStateFileName);
            return System.IO.File.Exists(p) && InstallStateIO.Load(p).HdEnabled;
        }
        catch (Exception ex)
        {
            Log($"HD state read failed: {ex.Message}");
            return false;
        }
    }

    HdSpaceReport? HdSpace(string install)
    {
        try
        {
            return _manifest is null
                ? null
                : ContentInstallService.PreflightHd(_manifest, install);
        }
        catch (Exception ex)
        {
            Log($"HD preflight failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Paint both HD rows: the settings card and the download card.</summary>
    void RefreshHdTextures()
    {
        var boxes = new[] { ChkHdTextures, ChkHdTexturesInstall };
        var labels = new[] { TxtHdTextures, TxtHdTexturesInstall };
        if (boxes[0] is null && boxes[1] is null)
            return;

        var install = InstallRoot();
        if (string.IsNullOrWhiteSpace(install))
        {
            SetHdRow(boxes, labels, false, false, Loc.Get("hd_no_install"));
            return;
        }

        var stateFile = System.IO.Path.Combine(install, ProductConstants.InstallStateFileName);
        if (!System.IO.File.Exists(stateFile) && !NeedsSetup(install))
        {
            var present = HdTextureSet.AnyPresent(install);
            var spaceLocal = HdSpace(install);
            SetHdRow(boxes, labels, present, false,
                present
                    ? Loc.Format("hd_on_fmt", spaceLocal?.TotalGiB ?? 0)
                    : Loc.Get("hd_unavailable"));
            return;
        }

        var enabled = HdEnabledOnDisk(install);
        if (_hdPending == HdPending.Enable) enabled = true;
        if (_hdPending == HdPending.Disable) enabled = false;

        var space = HdSpace(install);
        if (space is null || !space.Available)
        {
            SetHdRow(boxes, labels, enabled, false, Loc.Get("hd_unavailable"));
            return;
        }

        string text;
        if (_hdPending == HdPending.Enable)
            text = Loc.Get("hd_queued_on");
        else if (_hdPending == HdPending.Disable)
            text = Loc.Get("hd_queued_off");
        else if (enabled)
            text = Loc.Format("hd_on_fmt", space.TotalGiB);
        else
            text = Loc.Format("hd_off_fmt", space.MissingGiB, space.FreeGiB);

        SetHdRow(boxes, labels, enabled, true, text);
    }

    void SetHdRow(CheckBox?[] boxes, TextBlock?[] labels, bool check, bool on, string text)
    {
        _suppressHdEvent = true;
        foreach (var b in boxes)
        {
            if (b is null) continue;
            b.IsChecked = check;
            b.IsEnabled = on;
        }
        _suppressHdEvent = false;
        foreach (var l in labels)
        {
            if (l is not null)
                l.Text = text;
        }
    }

    void OnHdTexturesChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressHdEvent)
            return;

        var install = InstallRoot();
        if (string.IsNullOrWhiteSpace(install))
            return;

        var wanted = (sender as CheckBox)?.IsChecked == true;

        if (wanted && !ConfirmHdOn(install))
        {
            RefreshHdTextures();
            return;
        }
        if (!wanted && !ConfirmHdOff())
        {
            RefreshHdTextures();
            return;
        }

        // Mid-download: record it, act when the run ends.
        if (_installBusy)
        {
            _hdPending = wanted ? HdPending.Enable : HdPending.Disable;
            Log(wanted
                ? "HD textures queued; they download after the current files finish."
                : "HD textures will be removed when the current download finishes.");
            SetSimpleStatus(Loc.Get(wanted ? "hd_queued_on" : "hd_queued_off"));
            RefreshHdTextures();
            return;
        }

        ApplyHdChoice(install, wanted, kickInstall: true);
    }

    /// <summary>Refuse an HD install that will not fit, and say by how much.</summary>
    bool HdFitsOrWarn(string install)
    {
        var space = HdSpace(install);
        if (space is null || space.FitsOnDisk)
            return true;
        MessageBox.Show(
            this,
            Loc.Format("hd_no_space_fmt", space.MissingGiB, space.FreeGiB),
            Loc.Get("hd_textures"), MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    bool ConfirmHdOn(string install)
    {
        if (!HdFitsOrWarn(install))
            return false;
        var space = HdSpace(install);
        return MessageBox.Show(
            this,
            Loc.Format("hd_confirm_on_fmt", space?.MissingGiB ?? 0, space?.FreeGiB ?? 0),
            Loc.Get("hd_textures"),
            MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
    }

    bool ConfirmHdOff() =>
        MessageBox.Show(
            this, Loc.Get("hd_confirm_off"), Loc.Get("hd_textures"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    void ApplyHdChoice(string install, bool enabled, bool kickInstall)
    {
        try
        {
            var res = ContentInstallService.SetHdEnabled(install, enabled);
            if (!enabled)
            {
                var gib = res.BytesReclaimed / 1024.0 / 1024.0 / 1024.0;
                Log($"HD textures removed: {res.FilesRemoved} files, {gib:F1} GiB reclaimed");
                SetSimpleStatus(Loc.Format("hd_removed_fmt", res.FilesRemoved, gib));
                if (res.Failed.Count > 0)
                {
                    Log($"HD removal could not delete {res.Failed.Count} file(s); " +
                        "close the game and turn it off again");
                    MessageBox.Show(
                        this, Loc.Format("hd_remove_partial_fmt", res.Failed.Count),
                        Loc.Get("hd_textures"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"HD toggle failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, Loc.Get("hd_textures"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshHdTextures();
        if (enabled && kickInstall &&
            System.IO.File.Exists(System.IO.Path.Combine(install, ProductConstants.InstallStateFileName)))
            _ = RunInstallAsync(InstallMode.Full);
    }

    /// <summary>Run the choice the player made while a download was in flight.</summary>
    void ApplyPendingHdAction()
    {
        if (_hdPending == HdPending.None)
            return;
        var wanted = _hdPending == HdPending.Enable;
        _hdPending = HdPending.None;
        var install = InstallRoot();
        if (string.IsNullOrWhiteSpace(install))
            return;
        Log(wanted
            ? "Applying queued HD textures choice: on"
            : "Applying queued HD textures choice: off");
        ApplyHdChoice(install, wanted, kickInstall: wanted);
    }

    /// <summary>
    /// Tell existing players once that HD textures exist. A fresh install is
    /// offered HD in the install card, so this is only for people who already
    /// finished installing before the option shipped.
    /// </summary>
    void MaybeAnnounceHdTextures()
    {
        if (_settings.HdTexturesAnnounced)
            return;

        var install = InstallRoot();
        if (string.IsNullOrWhiteSpace(install))
            return;

        // Player installs only. Unenforced (master tree) is already complete on disk.
        var health = HealthForUi(install);
        if (!health.IsReady)
            return;
        if (!health.Enforced)
        {
            MarkHdAnnounced();
            return;
        }
        var space = HdSpace(install);
        if (space is null || !space.Available)
            return;
        if (HdEnabledOnDisk(install) || HdTextureSet.AnyPresent(install))
        {
            MarkHdAnnounced();
            return;
        }

        MarkHdAnnounced();
        var ok = MessageBox.Show(
            this,
            Loc.Format("hd_announce_fmt", space.MissingGiB),
            Loc.Get("hd_textures"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (ok != MessageBoxResult.Yes)
            return;
        // The notice already quoted the size; only the space refusal is left.
        if (!HdFitsOrWarn(install))
            return;
        ApplyHdChoice(install, true, kickInstall: true);
    }

    void MarkHdAnnounced()
    {
        _settings.HdTexturesAnnounced = true;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }
}
