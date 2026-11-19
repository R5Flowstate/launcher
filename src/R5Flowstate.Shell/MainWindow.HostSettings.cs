using System.Windows;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

// Server settings: modes declare r5f_setting_* vars in the playlist file and the
// launcher passes the host's choices to the dedi as playlist overrides.
public partial class MainWindow
{
    HostSettingsStore? _hostSettings;

    HostSettingsStore HostSettings
    {
        get
        {
            if (_hostSettings is null)
            {
                HostSettingsStore.Logger = Log;
                _hostSettings = HostSettingsStore.Load();
            }
            return _hostSettings;
        }
    }

    IReadOnlyList<PlaylistSetting> HostSettingsFor(string? playlistId) =>
        string.IsNullOrWhiteSpace(playlistId)
            ? Array.Empty<PlaylistSetting>()
            : _catalog.Find(playlistId.Trim())?.Settings ?? Array.Empty<PlaylistSetting>();

    IReadOnlyList<KeyValuePair<string, string>> HostOverridesFor(string? playlistId)
    {
        var settings = HostSettingsFor(playlistId);
        if (settings.Count == 0)
            return Array.Empty<KeyValuePair<string, string>>();
        return PlaylistSetting.ToOverrides(settings, HostSettings.ValuesFor(playlistId!.Trim(), settings));
    }

    /// <summary>The playlist the next server launch uses in the current layout.</summary>
    string HostSettingsPlaylistId() =>
        _settings.SimpleMode ? _selectedMode?.PlaylistId ?? string.Empty : SelectedPlaylistId();

    // Always offered: the dedicated server's launch line lives there even for modes without settings.
    void RefreshHostSettingsButtons()
    {
        var vis = Visibility.Visible;
        if (BtnSimpleHostSettings is not null)
            BtnSimpleHostSettings.Visibility = vis;
        if (SepSimpleHostSettings is not null)
            SepSimpleHostSettings.Visibility = vis;
        if (BtnDediHostSettings is not null)
            BtnDediHostSettings.Visibility = vis;
    }

    void OnHostSettingsClick(object sender, RoutedEventArgs e)
    {
        PersistSettingsFromUi();
        var playlist = HostSettingsPlaylistId();
        var settings = HostSettingsFor(playlist);

        var entry = _catalog.Find(playlist);
        var title = entry is null || string.IsNullOrWhiteSpace(entry.Title) ? playlist : entry.Title;
        var dlg = new HostSettingsWindow { Owner = this };
        void LoadDediLine() => dlg.LoadDediArgs(
            TxtDediArgs?.Text ?? _settings.DediLaunchArguments ?? string.Empty,
            _settings.DediDisabledArgs,
            BuildDediArgs(null, NullIfBlank(playlist), raw: true),
            DediAppliedLine(playlist));
        dlg.SettingsChanged += () =>
        {
            RefreshArgPreviews();
            LoadDediLine();
        };
        dlg.DediArgsChanged += (extra, disabled) =>
        {
            OnDediArgsChanged(extra, disabled);
            dlg.SetDediApplied(DediAppliedLine(playlist));
        };
        dlg.Load(
            HostSettings,
            playlist,
            title,
            settings,
            id => _catalog.Find(id)?.Settings,
            overrides => FormatHostArgs(playlist, overrides),
            TxtInstallRoot.Text.Trim());
        LoadDediLine();
        dlg.ShowDialog();
        RefreshArgPreviews();
    }

    /// <summary>What a headless host adds to its r5apex_ds.exe line for these settings.</summary>
    static string FormatHostArgs(string playlist, IReadOnlyList<KeyValuePair<string, string>> overrides)
    {
        var parts = new List<string> { "+launchplaylist", playlist };
        foreach (var kv in overrides)
        {
            parts.Add("+playlist_override_set");
            parts.Add(kv.Key);
            parts.Add(kv.Value);
        }
        return string.Join(' ', parts);
    }
}
