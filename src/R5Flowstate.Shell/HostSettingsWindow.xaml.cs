using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>
/// Server settings for one playlist, built from the r5f_setting_* declarations
/// in the playlist file. Every change that validates is stored at once; the
/// next server launch carries it.
/// </summary>
public partial class HostSettingsWindow : Window
{
    sealed class Row
    {
        public required PlaylistSetting Setting { get; init; }
        public required FrameworkElement Editor { get; init; }
        public required TextBlock Note { get; init; }
    }

    readonly List<Row> _rows = new();
    HostSettingsStore _store = null!;
    string _playlist = string.Empty;
    IReadOnlyList<PlaylistSetting> _settings = Array.Empty<PlaylistSetting>();
    Func<string, IReadOnlyList<PlaylistSetting>?> _settingsFor = _ => null;
    Func<IReadOnlyList<KeyValuePair<string, string>>, string> _formatArgs = _ => string.Empty;
    string _installRoot = string.Empty;
    bool _loading;

    public event Action? SettingsChanged;

    public HostSettingsWindow()
    {
        InitializeComponent();
    }

    public void Load(
        HostSettingsStore store,
        string playlist,
        string title,
        IReadOnlyList<PlaylistSetting> settings,
        Func<string, IReadOnlyList<PlaylistSetting>?> settingsFor,
        Func<IReadOnlyList<KeyValuePair<string, string>>, string> formatArgs,
        string installRoot)
    {
        _store = store;
        _installRoot = installRoot;
        _playlist = playlist;
        _settings = settings;
        _settingsFor = settingsFor;
        _formatArgs = formatArgs;
        TxtMode.Text = title;
        BuildRows(_store.ValuesFor(playlist, settings));
        RefreshPresets(null);
        RefreshArgs();
    }

    void BuildRows(IReadOnlyDictionary<string, string> values)
    {
        _loading = true;
        _rows.Clear();
        GridSettings.Children.Clear();
        GridSettings.RowDefinitions.Clear();

        for (var i = 0; i < _settings.Count; i++)
        {
            var s = _settings[i];
            values.TryGetValue(s.Var, out var value);
            value ??= s.Default;

            GridSettings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = s.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 12, 6),
                TextWrapping = TextWrapping.Wrap,
                ToolTip = s.Var,
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            GridSettings.Children.Add(label);

            var note = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMuted"),
            };
            Grid.SetRow(note, i);
            Grid.SetColumn(note, 2);
            GridSettings.Children.Add(note);

            FrameworkElement editor = s.Kind switch
            {
                PlaylistSettingKind.Bool => MakeCheck(value),
                PlaylistSettingKind.Choice or PlaylistSettingKind.Weapon => MakeChoice(s, value),
                _ => MakeText(value),
            };
            editor.Margin = new Thickness(0, 4, 0, 4);
            editor.ToolTip = s.RangeText.Length > 0 ? s.RangeText : s.Var;
            Grid.SetRow(editor, i);
            Grid.SetColumn(editor, 1);
            GridSettings.Children.Add(editor);

            var row = new Row { Setting = s, Editor = editor, Note = note };
            _rows.Add(row);
            UpdateNote(row);
        }
        _loading = false;
    }

    CheckBox MakeCheck(string value)
    {
        var box = new CheckBox { IsChecked = value == "1", VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("BaseCheckBox") is Style style)
            box.Style = style;
        box.Checked += (_, _) => OnEdited();
        box.Unchecked += (_, _) => OnEdited();
        return box;
    }

    ComboBox MakeChoice(PlaylistSetting s, string value)
    {
        var options = SettingOptions.For(s, _installRoot, Loc.Code);
        var combo = new ComboBox { ItemsSource = options, Height = 30, SelectedItem = options.FirstOrDefault(o => o.Value == value) };
        if (TryFindResource("DarkCombo") is Style style)
            combo.Style = style;
        combo.SelectionChanged += (_, _) => OnEdited();
        return combo;
    }

    TextBox MakeText(string value)
    {
        var box = new TextBox { Text = value, Height = 30, FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Center };
        if (TryFindResource("BaseTextBox") is Style style)
            box.Style = style;
        box.TextChanged += (_, _) => OnEdited();
        return box;
    }

    static string ValueOf(Row row) => row.Editor switch
    {
        CheckBox c => c.IsChecked == true ? "1" : "0",
        ComboBox c => (c.SelectedItem as SettingOption)?.Value ?? string.Empty,
        TextBox t => t.Text?.Trim() ?? string.Empty,
        _ => string.Empty,
    };

    Dictionary<string, string> CurrentValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
            values[row.Setting.Var] = ValueOf(row);
        return values;
    }

    void UpdateNote(Row row)
    {
        var value = ValueOf(row);
        if (!row.Setting.Validate(value, out var reason))
        {
            row.Note.Text = reason;
            row.Note.Foreground = (Brush)FindResource("DangerLine");
            return;
        }
        row.Note.Foreground = (Brush)FindResource("TextMuted");
        row.Note.Text = row.Setting.IsDefault(value)
            ? Loc.Get("host_setting_default")
            : Loc.Format("host_setting_default_is", DisplayValue(row.Setting, row.Setting.Default));
    }

    string DisplayValue(PlaylistSetting s, string value) => s.Kind switch
    {
        PlaylistSettingKind.Bool => value == "1" ? Loc.Get("on") : Loc.Get("off"),
        PlaylistSettingKind.Choice or PlaylistSettingKind.Weapon =>
            SettingOptions.For(s, _installRoot, Loc.Code).FirstOrDefault(o => o.Value == value)?.Label ?? value,
        _ => value,
    };

    void OnEdited()
    {
        if (_loading)
            return;
        foreach (var row in _rows)
            UpdateNote(row);
        _store.SetValues(_playlist, _settings, CurrentValues());
        RefreshArgs();
        SettingsChanged?.Invoke();
    }

    void RefreshArgs()
    {
        var overrides = PlaylistSetting.ToOverrides(_settings, CurrentValues());
        TxtArgs.Text = _formatArgs(overrides);
        var invalid = _rows.Count(r => !r.Setting.Validate(ValueOf(r), out _));
        TxtStatus.Text = invalid > 0
            ? Loc.Format("host_settings_invalid", invalid)
            : Loc.Format("host_settings_changed", overrides.Count);
    }

    void RefreshPresets(string? select)
    {
        var presets = _store.PresetsFor(_playlist);
        CmbPreset.ItemsSource = presets;
        if (select is not null)
            CmbPreset.SelectedItem = presets.FirstOrDefault(p => p.Name == select);
    }

    void ApplyValues(IReadOnlyDictionary<string, string> values)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _settings)
            merged[s.Var] = values.TryGetValue(s.Var, out var v) ? v : s.Default;
        BuildRows(merged);
        OnEdited();
    }

    void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbPreset.SelectedItem is not HostPreset preset)
            return;
        ApplyValues(preset.Values);
        TxtStatus.Text = Loc.Format("host_preset_loaded", preset.Name);
    }

    void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var name = HostSettingsStore.CleanPresetName(CmbPreset.Text);
        if (name.Length == 0)
        {
            TxtStatus.Text = Loc.Get("host_preset_need_name");
            return;
        }
        var preset = _store.SavePreset(name, _playlist, _settings, CurrentValues());
        _loading = true;
        RefreshPresets(preset.Name);
        _loading = false;
        TxtStatus.Text = Loc.Format("host_preset_saved", preset.Name);
    }

    void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (CmbPreset.SelectedItem is not HostPreset preset)
            return;
        _store.DeletePreset(preset);
        _loading = true;
        RefreshPresets(null);
        CmbPreset.Text = string.Empty;
        _loading = false;
        TxtStatus.Text = Loc.Format("host_preset_deleted", preset.Name);
    }

    void OnImportPreset(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = Loc.Get("host_preset_filter") + " (*" + HostSettingsStore.PresetExtension + ")|*" + HostSettingsStore.PresetExtension,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var preset = HostSettingsStore.ImportPreset(dlg.FileName, _settingsFor, out var error);
        if (preset is null)
        {
            TxtStatus.Text = Loc.Format("host_preset_import_failed", error);
            return;
        }
        if (!string.Equals(preset.Playlist, _playlist, StringComparison.OrdinalIgnoreCase))
        {
            TxtStatus.Text = Loc.Format("host_preset_other_mode", preset.Playlist);
            return;
        }
        var saved = _store.AddImported(preset, _settings);
        _loading = true;
        RefreshPresets(saved.Name);
        _loading = false;
        ApplyValues(saved.Values);
        TxtStatus.Text = Loc.Format("host_preset_loaded", saved.Name);
    }

    void OnExportPreset(object sender, RoutedEventArgs e)
    {
        var name = HostSettingsStore.CleanPresetName(CmbPreset.Text);
        if (name.Length == 0)
            name = _playlist;
        var dlg = new SaveFileDialog
        {
            FileName = name + HostSettingsStore.PresetExtension,
            DefaultExt = HostSettingsStore.PresetExtension,
            Filter = Loc.Get("host_preset_filter") + " (*" + HostSettingsStore.PresetExtension + ")|*" + HostSettingsStore.PresetExtension,
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var preset = new HostPreset { Name = name, Playlist = _playlist };
        foreach (var kv in PlaylistSetting.ToOverrides(_settings, CurrentValues()))
            preset.Values[kv.Key] = kv.Value;
        try
        {
            HostSettingsStore.ExportPreset(dlg.FileName, preset);
            TxtStatus.Text = Loc.Format("host_preset_exported", Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Loc.Format("host_preset_export_failed", ex.Message);
        }
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        ApplyValues(new Dictionary<string, string>());
        TxtStatus.Text = Loc.Get("host_settings_reset_done");
    }

    void OnCopyArgs(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TxtArgs.Text ?? string.Empty);
            TxtStatus.Text = Loc.Get("host_settings_copied");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = ex.Message;
        }
    }

    void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
