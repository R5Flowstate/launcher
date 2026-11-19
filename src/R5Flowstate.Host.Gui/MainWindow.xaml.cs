using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Host;
using R5Flowstate.Spawn;

namespace R5Flowstate.HostGui;

public partial class MainWindow : Window
{
    const int MaxConsoleLines = 3000;

    sealed class Running
    {
        public required DediSupervisor Supervisor { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
        public Task? Task { get; set; }
        public string State { get; set; } = "starting";
    }

    sealed class GuiPrefs
    {
        public string? LastPath { get; set; }
    }

    readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    readonly Dictionary<string, LinkedList<string>> _console = new(StringComparer.Ordinal);
    readonly Dictionary<string, (PlaylistSetting Setting, FrameworkElement Control)> _settingControls = new(StringComparer.Ordinal);
    readonly DispatcherTimer _updateTimer = new();
    readonly DispatcherTimer _consoleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    string _path = string.Empty;
    HostConfig _cfg = new();
    PlaylistCatalog _catalog = new();
    IReadOnlyList<ModPackage> _packages = Array.Empty<ModPackage>();
    HostUpdateCheck? _lastCheck;
    readonly ToolUpdate _tool = new();
    GuiPrefs _prefs = new();
    bool _busy;
    bool _loadingForm;
    bool _consoleDirty;
    string? _formInstance;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public MainWindow()
    {
        InitializeComponent();
        _consoleTimer.Tick += (_, _) => FlushConsole();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveForm();
        };
        _updateTimer.Tick += async (_, _) => await OnUpdateTimerAsync();
    }

    static string PrefsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "R5FlowstateServer", "gui.json");

    void LoadPrefs()
    {
        try
        {
            if (File.Exists(PrefsPath))
                _prefs = JsonSerializer.Deserialize<GuiPrefs>(File.ReadAllText(PrefsPath)) ?? new GuiPrefs();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _prefs = new GuiPrefs();
        }
    }

    void SavePrefs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(_prefs));
        }
        catch (IOException)
        {
        }
    }

    // -------------------------------------------------------------- lifecycle

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadPrefs();
        Title = "R5Flowstate Server " + _tool.CurrentVersion;
        _consoleTimer.Start();
        _ = CheckToolAsync();
        _ = LoadPackagesAsync();
        var start = !string.IsNullOrWhiteSpace(_prefs.LastPath) ? _prefs.LastPath : ServerFolderAboveTool();
        if (!string.IsNullOrWhiteSpace(start))
        {
            TxtPath.Text = start;
            await OpenPathAsync(start!);
        }
        else
        {
            SetStatus("Pick an empty folder for the server, then Install.");
            RefreshButtons();
        }
    }

    /// <summary>The server zip carries the tool in ServerTool\ (portable: ServerTool\current\ at run time).</summary>
    static string? ServerFolderAboveTool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 3 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ProcessSpawner.DediExeName)))
                return dir.FullName;
        }
        return null;
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        FlushSave();
        var live = _running.Values.Count(r => r.Supervisor.IsRunning);
        if (live == 0)
            return;
        if (MessageBox.Show(this, $"{live} server(s) are running. Stop them and close?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        foreach (var r in _running.Values)
        {
            r.Cancel.Cancel();
            r.Supervisor.StopAsync().Wait(TimeSpan.FromSeconds(25));
        }
    }

    // -------------------------------------------------------------- install bar

    void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnPathCommitted(sender, e);
    }

    async void OnPathCommitted(object sender, RoutedEventArgs e)
    {
        var p = TxtPath.Text.Trim();
        if (p.Length == 0 || string.Equals(p, _path, StringComparison.OrdinalIgnoreCase))
            return;
        await OpenPathAsync(p);
    }

    async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Server folder" };
        if (!string.IsNullOrWhiteSpace(_path))
            dlg.InitialDirectory = _path;
        if (dlg.ShowDialog(this) != true)
            return;
        TxtPath.Text = dlg.FolderName;
        await OpenPathAsync(dlg.FolderName);
    }

    async Task OpenPathAsync(string path)
    {
        if (_running.Values.Any(r => r.Supervisor.IsRunning))
        {
            MessageBox.Show(this, "Stop the running servers before switching folders.", Title);
            TxtPath.Text = _path;
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetStatus("Not a valid folder: " + ex.Message);
            return;
        }
        if (InstallPathPolicy.IsForbidden(full, AppContext.BaseDirectory))
        {
            SetStatus("That folder cannot hold a server (Program Files, AppData or this app's folder).");
            return;
        }

        _path = full;
        _prefs.LastPath = full;
        SavePrefs();
        _running.Clear();
        _console.Clear();

        _cfg = HostConfig.Load(full);
        if (_cfg.Instances.Count == 0)
            _cfg.Instances.Add(new HostInstance { Name = "main" });
        CmbRing.SelectedIndex = _cfg.Ring == HostRing.Playtest ? 1 : 0;
        ReloadCatalog();
        RefreshInstanceList("main");
        RefreshVersions();
        ScheduleUpdateTimer();
        await CheckAsync(quiet: true);
    }

    bool IsInstalled => !string.IsNullOrEmpty(_path) && File.Exists(HostState.StatePath(_path))
                        && File.Exists(Path.Combine(_path, ProcessSpawner.DediExeName));

    void OnRingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(_path) || _loadingForm)
            return;
        var ring = CmbRing.SelectedIndex == 1 ? HostRing.Playtest : HostRing.Live;
        if (ring == _cfg.Ring)
            return;
        if (ring == HostRing.Playtest && MessageBox.Show(this,
                "Playtest builds are unfinished and only playtest players can see these servers. Switch?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            CmbRing.SelectedIndex = 0;
            return;
        }
        _cfg.Ring = ring;
        SaveConfig();
        _ = CheckAsync(quiet: false);
    }

    async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync(quiet: false);

    async Task CheckAsync(bool quiet)
    {
        if (string.IsNullOrEmpty(_path) || _busy)
            return;
        try
        {
            SetBusy(true, "Checking for updates...");
            using var fetcher = new FileSystemFetcher(TimeSpan.FromSeconds(20));
            _lastCheck = await Task.Run(() => HostInstaller.CheckAsync(_path, _cfg, fetcher)).ConfigureAwait(true);
            var tip = string.Join("   ", _lastCheck.Tracks.Select(t => t.Track + " " + t.Tip.CatalogVersion));
            RefreshUpdateBanner();
            if (!IsInstalled)
                SetStatus("Ready to install " + tip + ".");
            else if (_lastCheck.UpdateAvailable)
                SetStatus("Update available: " + tip + ".");
            else if (!quiet)
                SetStatus("Up to date.");
            else
                SetStatus(string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("Could not read the server channel: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void OnInstall(object sender, RoutedEventArgs e) =>
        await InstallAsync(repair: IsInstalled && _lastCheck is not { UpdateAvailable: true });

    async Task InstallAsync(bool repair)
    {
        if (string.IsNullOrEmpty(_path))
        {
            SetStatus("Pick a folder first.");
            return;
        }
        var wasRunning = _running.Where(kv => kv.Value.Supervisor.IsRunning).Select(kv => kv.Key).ToList();
        if (wasRunning.Count > 0)
        {
            if (MessageBox.Show(this, "Updating stops the running servers and starts them again. Continue?",
                    Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            await StopAllAsync();
        }

        try
        {
            SetBusy(true, IsInstalled ? "Updating..." : "Installing...");
            SaveConfig();
            foreach (var inst in _cfg.Instances)
            {
                if (!File.Exists(InstanceCfg.PathFor(_path, inst)))
                    InstanceCfg.Write(_path, inst, new InstanceCfg.Secrets { RconPassword = InstanceCfg.NewRconPassword() });
            }

            var last = DateTime.MinValue;
            var progress = new Progress<ContentInstallProgress>(p =>
            {
                if (DateTime.UtcNow - last < TimeSpan.FromMilliseconds(200))
                    return;
                last = DateTime.UtcNow;
                var (cur, tot) = p.JobTotal > 0 ? (p.JobCurrent, p.JobTotal) : (p.Current, p.Total);
                if (tot > 0)
                    BarProgress.Value = Math.Clamp(100.0 * cur / tot, 0, 100);
                var msg = string.IsNullOrWhiteSpace(p.Message) ? p.Phase : p.Message;
                if (tot >= 100_000_000 && p.Unit == ProgressUnit.Bytes)
                    msg = $"{p.Track}: {msg}  {cur / 1e9:0.00} / {tot / 1e9:0.00} GB";
                else if (!string.IsNullOrWhiteSpace(p.Track))
                    msg = p.Track + ": " + msg;
                TxtStatus.Text = msg;
            });

            var path = _path;
            var cfg = _cfg;
            _lastCheck = await Task.Run(async () =>
            {
                using var fetcher = new FileSystemFetcher();
                var check = await HostInstaller.UpdateAsync(path, cfg, fetcher, progress, repair).ConfigureAwait(false);
                using var mods = new HostMods(path);
                await mods.SyncAsync(null, CancellationToken.None).ConfigureAwait(false);
                return check;
            }).ConfigureAwait(true);

            BarProgress.Value = 100;
            RefreshUpdateBanner();
            SetStatus("Installed " + HostRunner.Describe(_lastCheck) + ".");
            ReloadCatalog();
            LoadInstanceForm(SelectedInstanceName());
            RefreshVersions();
            RefreshMods();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("Install failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        foreach (var name in wasRunning)
            StartInstance(name);
    }

    void RefreshVersions()
    {
        if (string.IsNullOrEmpty(_path))
            return;
        var state = HostState.Load(_path);
        TxtVersions.Text = state.Tracks.Count == 0
            ? "Not installed in " + _path
            : $"{HostRings.Name(_cfg.Ring)}  ·  {state.MarketingTag ?? "-"}  ·  " +
              string.Join("  ·  ", state.Tracks.Select(t => t.Key + " " + t.Value.CatalogVersion)) +
              $"  ·  gate {state.GateName ?? "-"}";
    }

    /// <summary>New text whenever the channel tip moved past what is installed; updating stays manual.</summary>
    void RefreshUpdateBanner()
    {
        if (_lastCheck is not { UpdateAvailable: true } || !IsInstalled)
        {
            TxtUpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var state = HostState.Load(_path);
        var parts = _lastCheck.Tracks
            .Where(t => !t.UpToDate)
            .Select(t => state.Tracks.TryGetValue(t.Track, out var have) && !string.IsNullOrEmpty(have.CatalogVersion)
                ? $"{t.Track} {have.CatalogVersion} -> {t.Tip.CatalogVersion}"
                : $"{t.Track} {t.Tip.CatalogVersion}");
        var live = _running.Values.Count(r => r.Supervisor.IsRunning);
        TxtUpdateBanner.Text = "New update available: " + string.Join(", ", parts)
            + (string.IsNullOrWhiteSpace(_lastCheck.Channel.MarketingTag) ? "" : " (" + _lastCheck.Channel.MarketingTag + ")")
            + ". Press Update" + (live > 0 ? " -- running servers restart." : ".");
        TxtUpdateBanner.Visibility = Visibility.Visible;
    }

    void ScheduleUpdateTimer()
    {
        _updateTimer.Stop();
        _updateTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_cfg.UpdateCheckMinutes, 5, 24 * 60));
        _updateTimer.Start();
    }

    async Task OnUpdateTimerAsync()
    {
        await CheckToolAsync();
        if (_busy || !IsInstalled)
            return;
        await CheckAsync(quiet: true);
        await LoadPackagesAsync();
    }

    async Task CheckToolAsync()
    {
        if (!_tool.IsInstalled)
            return;
        try
        {
            var ver = await Task.Run(() => _tool.CheckAndDownloadAsync()).ConfigureAwait(true);
            if (ver is null)
                return;
            TxtToolUpdate.Text = "Server tool " + ver + " is ready.";
            PanelToolUpdate.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("Server tool update check failed: " + ex.Message);
        }
    }

    async void OnToolUpdate(object sender, RoutedEventArgs e)
    {
        if (_tool.ReadyVersion is null || _busy)
            return;
        var live = _running.Values.Count(r => r.Supervisor.IsRunning);
        if (live > 0)
        {
            if (MessageBox.Show(this, $"{live} server(s) are running. Stop them and restart the tool to update?", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await StopAllAsync();
        }
        _tool.ApplyAndRestart();
    }

    // -------------------------------------------------------------- catalog

    void ReloadCatalog()
    {
        try
        {
            var mods = ModsStore.Discover(_path)
                .Where(m => m.Maps.Count > 0)
                .Select(m => new ModPlaylistSource
                {
                    Id = m.Id,
                    Folder = Path.Combine(ModsStore.ModsDirectory(_path), m.FolderName),
                    Maps = m.Maps,
                })
                .ToList();
            _catalog = PlaylistCatalogLoader.Load(_path, "english", uiLoc: null, mods: mods);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _catalog = new PlaylistCatalog();
        }
        var was = _loadingForm;
        _loadingForm = true;
        try
        {
            CmbPlaylist.ItemsSource = _catalog.HostPlaylists;
        }
        finally
        {
            _loadingForm = was;
        }
    }

    // -------------------------------------------------------------- instances

    string? SelectedInstanceName() => (LstInstances.SelectedItem as ListBoxItem)?.Tag as string;

    HostInstance? SelectedInstance() =>
        SelectedInstanceName() is { } n ? _cfg.Find(n) : null;

    void RefreshInstanceList(string? select = null)
    {
        select ??= SelectedInstanceName();
        LstInstances.Items.Clear();
        foreach (var inst in _cfg.Instances)
        {
            var state = _running.TryGetValue(inst.Name, out var r) && r.Supervisor.IsRunning ? r.State : "stopped";
            var item = new ListBoxItem
            {
                Content = $"{inst.Name}   :{inst.Port}   {state}",
                Tag = inst.Name,
            };
            LstInstances.Items.Add(item);
            if (inst.Name == select)
                LstInstances.SelectedItem = item;
        }
        if (LstInstances.SelectedItem is null && LstInstances.Items.Count > 0)
            LstInstances.SelectedIndex = 0;
        RefreshButtons();
    }

    void OnInstanceSelected(object sender, SelectionChangedEventArgs e)
    {
        FlushSave();
        LoadInstanceForm(SelectedInstanceName());
        ShowConsole();
        RefreshButtons();
    }

    void LoadInstanceForm(string? name)
    {
        FlushSave();
        var inst = name is null ? null : _cfg.Find(name);
        _formInstance = inst?.Name;
        PanelInstance.IsEnabled = inst is not null;
        if (inst is null)
            return;

        _loadingForm = true;
        try
        {
            TxtInstanceTitle.Text = "SETTINGS  ·  " + inst.Name.ToUpperInvariant();
            TxtHostname.Text = inst.Hostname ?? string.Empty;
            TxtDescription.Text = inst.Description ?? string.Empty;
            TxtPort.Text = inst.Port.ToString(CultureInfo.InvariantCulture);
            CmbVisibility.SelectedIndex = inst.Visibility switch
            {
                SpireVisibility.Hidden => 1,
                SpireVisibility.Offline => 2,
                _ => 0,
            };
            TxtExtra.Text = inst.ExtraArgs ?? string.Empty;
            ChkAutoRestart.IsChecked = inst.AutoRestart;
            CmbTickRate.SelectedIndex = Math.Max(0, Array.IndexOf(TickRates.Offered,
                TickRates.Normalize(inst.TickRate, TickRates.Stock)));

            var secrets = string.IsNullOrEmpty(_path) ? new InstanceCfg.Secrets() : InstanceCfg.Read(_path, inst);
            PwdJoin.Password = secrets.ServerPassword ?? string.Empty;
            PwdRcon.Password = secrets.RconPassword ?? string.Empty;
            PwdStats.Password = secrets.StatsHostKey ?? string.Empty;

            CmbPlaylist.SelectedItem = _catalog.Find(inst.Playlist);
            if (CmbPlaylist.SelectedItem is null && CmbPlaylist.Items.Count > 0 && string.IsNullOrEmpty(inst.Playlist))
                CmbPlaylist.SelectedIndex = -1;
            FillMaps(inst.Map);
            BuildSettingsPanel(inst.PlaylistOverrides);
            BuildModsPanel(inst.Mods);
        }
        finally
        {
            _loadingForm = false;
        }
        RefreshArgs();
    }

    string? SelectedPlaylistId() => (CmbPlaylist.SelectedItem as PlaylistEntry)?.Id;

    void OnPlaylistChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingForm)
            return;
        var values = ReadSettingValues();
        var picks = ReadModPicks();
        _loadingForm = true;
        try
        {
            FillMaps(CmbMap.SelectedItem as string);
            BuildSettingsPanel(values);
            BuildModsPanel(picks);
        }
        finally
        {
            _loadingForm = false;
        }
        ScheduleSave();
    }

    void OnMapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingForm)
            return;
        var picks = ReadModPicks();
        _loadingForm = true;
        try
        {
            BuildModsPanel(picks);
        }
        finally
        {
            _loadingForm = false;
        }
        ScheduleSave();
    }

    void OnFormChanged(object sender, RoutedEventArgs e) => ScheduleSave();

    void ScheduleSave()
    {
        if (_loadingForm || _formInstance is null)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void FlushSave()
    {
        if (!_saveTimer.IsEnabled)
            return;
        _saveTimer.Stop();
        SaveForm();
    }

    readonly List<(InstalledMod Mod, CheckBox On, CheckBox Required, bool Forced)> _modControls = new();

    void BuildModsPanel(IReadOnlyList<InstanceMod> picks)
    {
        PanelInstanceMods.Children.Clear();
        _modControls.Clear();
        if (string.IsNullOrEmpty(_path) || !Directory.Exists(_path))
            return;
        var installed = ModsStore.Discover(_path);
        var forced = InstanceMods.ForcedMods(_path, installed, SelectedPlaylistId(), CmbMap.SelectedItem as string);
        TxtNoMods.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var mod in installed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var pick = picks.FirstOrDefault(p => string.Equals(p.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            var isForced = forced.TryGetValue(mod.Id, out var why);
            var label = (string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name) + "  " + mod.Version;
            var on = new CheckBox
            {
                Content = label,
                IsChecked = isForced || pick is not null,
                IsEnabled = !isForced,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = isForced ? "Always on: this server's " + why + " comes from this mod." : mod.Id,
            };
            var req = new CheckBox
            {
                Content = "Required",
                IsChecked = isForced || pick is null || pick.Required,
                IsEnabled = !isForced && on.IsChecked == true,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Players must have this mod to join. Unchecked: players may join without it.",
            };
            on.Checked += (_, _) => { req.IsEnabled = true; ScheduleSave(); };
            on.Unchecked += (_, _) => { req.IsEnabled = false; ScheduleSave(); };
            req.Checked += (_, _) => ScheduleSave();
            req.Unchecked += (_, _) => ScheduleSave();
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(req, 1);
            row.Children.Add(on);
            row.Children.Add(req);
            PanelInstanceMods.Children.Add(row);
            _modControls.Add((mod, on, req, isForced));
        }
    }

    List<InstanceMod> ReadModPicks() => _modControls
        .Where(c => !c.Forced && c.On.IsChecked == true)
        .Select(c => new InstanceMod { Id = c.Mod.Id, Required = c.Required.IsChecked == true })
        .ToList();

    /// <summary>Only maps this playlist runs on; a playlist pinned to one map selects it.</summary>
    void FillMaps(string? prefer)
    {
        var maps = _catalog.HostMapsFor(SelectedPlaylistId());
        CmbMap.ItemsSource = maps;
        var hit = maps.FirstOrDefault(m => string.Equals(m, prefer, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
            CmbMap.SelectedItem = hit;
        else if (maps.Count > 0)
            CmbMap.SelectedIndex = 0;
        CmbMap.IsEnabled = maps.Count > 1;
    }

    IReadOnlyList<PlaylistSetting> CurrentSettings() =>
        ForcedLegend.WithLegend(_catalog.Find(SelectedPlaylistId())?.Settings ?? Array.Empty<PlaylistSetting>(), _path);

    void BuildSettingsPanel(IReadOnlyDictionary<string, string>? values)
    {
        PanelSettings.Children.Clear();
        _settingControls.Clear();
        _capacityHint = new TextBlock
        {
            Style = (Style)FindResource("MonoNote"),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        PanelSettings.Children.Add(_capacityHint);
        var settings = CurrentSettings();
        TxtNoSettings.Visibility = settings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnResetSettings.Visibility = settings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var s in settings)
        {
            var value = values is not null && values.TryGetValue(s.Var, out var v) && s.Validate(v, out _) ? v : s.Default;
            var label = s.Label + (string.IsNullOrEmpty(s.RangeText) ? "" : "   (" + s.RangeText + ")");
            FrameworkElement control;
            switch (s.Kind)
            {
                case PlaylistSettingKind.Bool:
                    control = new CheckBox { Content = s.Label, IsChecked = value == "1", Margin = new Thickness(0, 0, 0, 8) };
                    PanelSettings.Children.Add(control);
                    break;
                case PlaylistSettingKind.Choice:
                case PlaylistSettingKind.Weapon:
                {
                    var options = SettingOptions.For(s, _path);
                    var combo = new ComboBox
                    {
                        ItemsSource = options,
                        SelectedItem = options.FirstOrDefault(o => o.Value == value),
                        Style = (Style)FindResource("DarkCombo"),
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    PanelSettings.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                    PanelSettings.Children.Add(combo);
                    control = combo;
                    break;
                }
                default:
                {
                    var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 8), ToolTip = s.Var };
                    PanelSettings.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel") });
                    PanelSettings.Children.Add(box);
                    control = box;
                    break;
                }
            }
            control.ToolTip ??= s.Var;
            switch (control)
            {
                case CheckBox cb:
                    cb.Checked += (_, _) => { RefreshLegendGate(); ScheduleSave(); };
                    cb.Unchecked += (_, _) => { RefreshLegendGate(); ScheduleSave(); };
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += (_, _) => ScheduleSave();
                    break;
                case TextBox tb:
                    tb.TextChanged += (_, _) => { UpdateCapacityHint(); ScheduleSave(); };
                    break;
            }
            _settingControls[s.Var] = (s, control);
        }
        RefreshLegendGate();
        UpdateCapacityHint();
    }

    TextBlock? _capacityHint;

    void UpdateCapacityHint()
    {
        if (_capacityHint is null)
            return;
        var settings = CurrentSettings();
        var values = ReadSettingValues();
        var conflict = HostCapacity.Conflict(settings, values);
        if (conflict is not null)
        {
            _capacityHint.Text = conflict;
            if (TryFindResource("DangerLine") is Brush danger)
                _capacityHint.Foreground = danger;
            return;
        }
        if (TryFindResource("TextMuted") is Brush muted)
            _capacityHint.Foreground = muted;
        var perTeam = HostCapacity.PerTeam(settings, values);
        _capacityHint.Text = perTeam ?? string.Empty;
    }

    void RefreshLegendGate()
    {
        if (_settingControls.TryGetValue(ForcedLegend.RefVar, out var legend) &&
            _settingControls.TryGetValue(ForcedLegend.ForceVar, out var force) && force.Item2 is CheckBox cb)
        {
            legend.Item2.IsEnabled = cb.IsChecked == true;
        }
    }

    void OnResetSettings(object sender, RoutedEventArgs e)
    {
        BuildSettingsPanel(null);
        ScheduleSave();
    }

    Dictionary<string, string> ReadSettingValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (var, (_, control)) in _settingControls)
        {
            values[var] = control switch
            {
                CheckBox cb => cb.IsChecked == true ? "1" : "0",
                ComboBox combo => (combo.SelectedItem as SettingOption)?.Value ?? string.Empty,
                TextBox tb => tb.Text.Trim(),
                _ => string.Empty,
            };
        }
        return values;
    }

    void SaveForm()
    {
        var inst = _formInstance is null ? null : _cfg.Find(_formInstance);
        if (inst is null || string.IsNullOrEmpty(_path))
            return;

        if (!int.TryParse(TxtPort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1024 or > 65535)
        {
            SetStatus("Port must be 1024-65535.");
            return;
        }
        if (_cfg.Instances.Any(o => o != inst && o.Port == port))
        {
            SetStatus("Port " + port + " is used by another instance.");
            return;
        }

        var settings = CurrentSettings();
        var values = ReadSettingValues();
        foreach (var s in settings)
        {
            if (values.TryGetValue(s.Var, out var v) && !s.Validate(v, out var reason))
            {
                SetStatus($"{s.Label}: {reason}.");
                return;
            }
        }
        var conflict = HostCapacity.Conflict(settings, values);
        if (conflict is not null)
        {
            SetStatus(conflict + ".");
            return;
        }

        var secrets = new InstanceCfg.Secrets
        {
            ServerPassword = NullIfEmpty(PwdJoin.Password),
            RconPassword = NullIfEmpty(PwdRcon.Password),
            StatsHostKey = NullIfEmpty(PwdStats.Password),
        };
        try
        {
            foreach (var (what, val, max) in new[]
                     {
                         ("Server name", TxtHostname.Text.Trim(), 128),
                         ("Description", TxtDescription.Text.Trim(), 256),
                         ("Join password", secrets.ServerPassword ?? "", 128),
                         ("RCON password", secrets.RconPassword ?? "", 128),
                         ("Stats key", secrets.StatsHostKey ?? "", 128),
                     })
            {
                if (val.Length > 0 && !InstanceCfg.IsSafeValue(val, max))
                    throw new ArgumentException(what + " cannot contain quotes, ';' or backslashes.");
            }

            inst.Port = port;
            inst.Hostname = NullIfEmpty(TxtHostname.Text.Trim());
            inst.Description = NullIfEmpty(TxtDescription.Text.Trim());
            inst.Visibility = CmbVisibility.SelectedIndex switch
            {
                1 => SpireVisibility.Hidden,
                2 => SpireVisibility.Offline,
                _ => SpireVisibility.Public,
            };
            inst.Playlist = SelectedPlaylistId();
            inst.Map = CmbMap.SelectedItem as string;
            inst.ExtraArgs = NullIfEmpty(TxtExtra.Text.Trim());
            inst.AutoRestart = ChkAutoRestart.IsChecked == true;
            inst.TickRate = CmbTickRate.SelectedIndex >= 0 && CmbTickRate.SelectedIndex < TickRates.Offered.Length
                ? TickRates.Offered[CmbTickRate.SelectedIndex]
                : TickRates.Stock;
            inst.PlaylistOverrides = PlaylistSetting.ToOverrides(settings, values)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            inst.Mods = ReadModPicks();

            InstanceCfg.Write(_path, inst, secrets);
            SaveConfig();
            SetStatus($"Saved {inst.Name}." + (IsRunningInstance(inst.Name) ? " Restart it to apply." : ""));
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
        }
        RefreshInstanceLabels();
        RefreshArgs();
    }

    void RefreshInstanceLabels()
    {
        foreach (var item in LstInstances.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is not string name || _cfg.Find(name) is not { } inst)
                continue;
            var state = _running.TryGetValue(inst.Name, out var r) && r.Supervisor.IsRunning ? r.State : "stopped";
            item.Content = $"{inst.Name}   :{inst.Port}   {state}";
        }
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    void RefreshArgs()
    {
        var inst = SelectedInstance();
        if (inst is null)
        {
            TxtArgs.Text = string.Empty;
            return;
        }
        using var s = new DediSupervisor(_path, _cfg, inst);
        TxtArgs.Text = ProcessSpawner.DediExeName + " " + DediSupervisor.Describe(s.BuildArgs());
    }

    void OnAddInstance(object sender, RoutedEventArgs e)
    {
        var name = TxtNewInstance.Text.Trim().ToLowerInvariant();
        if (!HostInstance.IsValidName(name))
        {
            SetStatus("Instance names are 1-24 of a-z 0-9 _ -.");
            return;
        }
        if (_cfg.Find(name) is not null)
        {
            SetStatus(name + " already exists.");
            return;
        }
        if (_cfg.Instances.Count >= HostConfig.MaxInstances)
        {
            SetStatus("At most " + HostConfig.MaxInstances + " instances.");
            return;
        }
        var port = LaunchArgs.DefaultDediPort;
        while (_cfg.Instances.Any(i => i.Port == port))
            port++;
        var inst = new HostInstance { Name = name, Port = port };
        _cfg.Instances.Add(inst);
        if (!string.IsNullOrEmpty(_path))
            InstanceCfg.Write(_path, inst, new InstanceCfg.Secrets { RconPassword = InstanceCfg.NewRconPassword() });
        SaveConfig();
        TxtNewInstance.Text = string.Empty;
        RefreshInstanceList(name);
    }

    void OnRemoveInstance(object sender, RoutedEventArgs e)
    {
        var inst = SelectedInstance();
        if (inst is null)
            return;
        if (IsRunningInstance(inst.Name))
        {
            SetStatus("Stop " + inst.Name + " first.");
            return;
        }
        if (MessageBox.Show(this, "Remove instance " + inst.Name + " and its settings?", Title,
                MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        _cfg.Instances.Remove(inst);
        var file = InstanceCfg.PathFor(_path, inst);
        if (File.Exists(file))
            File.Delete(file);
        _console.Remove(inst.Name);
        SaveConfig();
        RefreshInstanceList();
    }

    void SaveConfig()
    {
        if (string.IsNullOrEmpty(_path))
            return;
        try
        {
            Directory.CreateDirectory(_path);
            _cfg.Save(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not save settings: " + ex.Message);
        }
    }

    // -------------------------------------------------------------- run

    bool IsRunningInstance(string name) => _running.TryGetValue(name, out var r) && r.Supervisor.IsRunning;

    void OnStart(object sender, RoutedEventArgs e)
    {
        if (SelectedInstanceName() is { } n)
            StartInstance(n);
    }

    async void OnStop(object sender, RoutedEventArgs e)
    {
        if (SelectedInstanceName() is { } n)
            await StopInstanceAsync(n);
    }

    async void OnRestart(object sender, RoutedEventArgs e)
    {
        if (SelectedInstanceName() is not { } n)
            return;
        await StopInstanceAsync(n);
        StartInstance(n);
    }

    void OnStartAll(object sender, RoutedEventArgs e)
    {
        foreach (var inst in _cfg.Instances)
            StartInstance(inst.Name);
    }

    async void OnStopAll(object sender, RoutedEventArgs e) => await StopAllAsync();

    void StartInstance(string name)
    {
        FlushSave();
        if (!IsInstalled)
        {
            SetStatus("Install the server first.");
            return;
        }
        if (_busy)
        {
            SetStatus("Wait for the current download to finish.");
            return;
        }
        if (IsRunningInstance(name))
            return;
        var inst = _cfg.Find(name);
        if (inst is null)
            return;
        if (DediProcesses.LivePids(_path).ContainsKey(name))
        {
            SetStatus(name + " is already running outside this window (r5f-host run?).");
            return;
        }
        if (!File.Exists(InstanceCfg.PathFor(_path, inst)))
            InstanceCfg.Write(_path, inst, new InstanceCfg.Secrets { RconPassword = InstanceCfg.NewRconPassword() });

        var sup = new DediSupervisor(_path, _cfg, inst);
        var run = new Running { Supervisor = sup, Cancel = new CancellationTokenSource() };
        sup.Event += ev => Dispatcher.BeginInvoke(() => OnDediEvent(ev));
        _running[name] = run;
        AppendConsole(name, "---- starting " + name + " ----");
        run.Task = Task.Run(async () =>
        {
            try
            {
                await sup.RunAsync(run.Cancel.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => AppendConsole(name, "[host] " + ex.Message));
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    run.State = "stopped";
                    AppendConsole(name, "---- " + name + " stopped ----");
                    RefreshInstanceList();
                });
            }
        });
        RefreshInstanceList();
    }

    async Task StopInstanceAsync(string name)
    {
        if (!_running.TryGetValue(name, out var r))
            return;
        r.Cancel.Cancel();
        await r.Supervisor.StopAsync().ConfigureAwait(true);
        if (r.Task is not null)
            await Task.WhenAny(r.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        r.Supervisor.Dispose();
        _running.Remove(name);
        RefreshInstanceList();
    }

    async Task StopAllAsync()
    {
        foreach (var name in _running.Keys.ToList())
            await StopInstanceAsync(name);
    }

    void OnDediEvent(DediEvent ev)
    {
        if (ev.Kind == DediEventKind.Line)
        {
            AppendConsole(ev.Instance, ev.Text);
            return;
        }
        AppendConsole(ev.Instance, "[host] " + ev.Kind.ToString().ToUpperInvariant() + " " + ev.Text);
        if (_running.TryGetValue(ev.Instance, out var r))
        {
            r.State = ev.Kind switch
            {
                DediEventKind.Ready => "running",
                DediEventKind.Starting => "starting",
                DediEventKind.Fatal => "error",
                DediEventKind.Exited => "restarting",
                DediEventKind.GaveUp => "crashed",
                _ => r.State,
            };
        }
        RefreshInstanceList();
    }

    void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnSendCommand(sender, e);
    }

    void OnSendCommand(object sender, RoutedEventArgs e)
    {
        var name = SelectedInstanceName();
        var cmd = TxtCommand.Text.Trim();
        if (name is null || cmd.Length == 0)
            return;
        if (!_running.TryGetValue(name, out var r) || !r.Supervisor.SendCommand(cmd))
        {
            SetStatus(name + " is not running.");
            return;
        }
        AppendConsole(name, "] " + cmd);
        TxtCommand.Text = string.Empty;
    }

    void AppendConsole(string instance, string line)
    {
        if (!_console.TryGetValue(instance, out var buf))
            _console[instance] = buf = new LinkedList<string>();
        buf.AddLast(line);
        while (buf.Count > MaxConsoleLines)
            buf.RemoveFirst();
        if (instance == SelectedInstanceName())
            _consoleDirty = true;
    }

    void ShowConsole()
    {
        _consoleDirty = true;
        FlushConsole();
    }

    void FlushConsole()
    {
        if (!_consoleDirty)
            return;
        _consoleDirty = false;
        var name = SelectedInstanceName();
        TxtConsoleTitle.Text = name is null ? "CONSOLE" : "CONSOLE  ·  " + name.ToUpperInvariant();
        TxtRunState.Text = name is not null && _running.TryGetValue(name, out var r) && r.Supervisor.IsRunning
            ? r.State
            : "stopped";
        var atBottom = TxtConsole.VerticalOffset + TxtConsole.ViewportHeight >= TxtConsole.ExtentHeight - 4;
        if (name is null || !_console.TryGetValue(name, out var buf))
        {
            TxtConsole.Text = string.Empty;
            return;
        }
        var sb = new StringBuilder();
        foreach (var l in buf)
            sb.Append(l).Append('\n');
        TxtConsole.Text = sb.ToString();
        if (atBottom)
            TxtConsole.ScrollToEnd();
    }

    // -------------------------------------------------------------- tabs

    void OnTabServers(object sender, RoutedEventArgs e)
    {
        PageServers.Visibility = Visibility.Visible;
        PageMods.Visibility = Visibility.Collapsed;
        TabServers.Style = (Style)FindResource("TabButtonOn");
        TabMods.Style = (Style)FindResource("TabButton");
    }

    void OnTabMods(object sender, RoutedEventArgs e)
    {
        PageServers.Visibility = Visibility.Collapsed;
        PageMods.Visibility = Visibility.Visible;
        TabServers.Style = (Style)FindResource("TabButton");
        TabMods.Style = (Style)FindResource("TabButtonOn");
        RefreshMods();
        if (_packages.Count == 0)
            _ = LoadPackagesAsync();
    }

    // -------------------------------------------------------------- mods

    void RefreshMods()
    {
        LstInstalledMods.Items.Clear();
        if (string.IsNullOrEmpty(_path) || !Directory.Exists(_path))
            return;
        using var mods = new HostMods(_path);
        var locked = mods.ReadLock().Pins;
        var required = ModsStore.ReadRequiredMods(_path).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outdated = new List<string>();
        foreach (var m in mods.Installed())
        {
            var pin = locked.TryGetValue(m.ThunderstoreFullName, out var v) ? "lock " + v : "unlocked";
            var latest = LatestVersion(m);
            var item = new ListBoxItem
            {
                Content = $"{m.FolderName,-34} {m.Version,-9} {(required.Contains(m.Id) ? "required" : "optional"),-9} {pin}"
                    + (latest is null ? "" : "   update -> " + latest),
                Tag = m,
            };
            if (latest is not null)
            {
                item.Foreground = (System.Windows.Media.Brush)FindResource("UpdateFill");
                outdated.Add(m.FolderName + " " + latest);
            }
            LstInstalledMods.Items.Add(item);
        }
        TabMods.Content = outdated.Count == 0 ? "Mods" : $"Mods ({outdated.Count})";
        TxtModUpdates.Text = outdated.Count == 0 ? string.Empty
            : $"{outdated.Count} mod update(s) available: {string.Join(", ", outdated)}. Press Update all.";
        TxtModUpdates.Visibility = outdated.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    string? LatestVersion(InstalledMod m)
    {
        if (string.IsNullOrEmpty(m.ThunderstoreFullName))
            return null;
        var pkg = _packages.FirstOrDefault(p => string.Equals(p.FullName, m.ThunderstoreFullName, StringComparison.OrdinalIgnoreCase));
        var latest = pkg?.Versions.FirstOrDefault()?.VersionNumber;
        return latest is not null && ModVersion.Compare(latest, m.ThunderstoreVersion) > 0 ? latest : null;
    }

    async Task LoadPackagesAsync()
    {
        try
        {
            TxtModDetail.Text = "Loading Thunderstore...";
            var path = string.IsNullOrEmpty(_path) ? Path.GetTempPath() : _path;
            _packages = await Task.Run(async () =>
            {
                using var mods = new HostMods(path);
                return await mods.CatalogAsync(CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(true);
            TxtModDetail.Text = _packages.Count + " packages.";
            FilterCatalog();
            RefreshMods();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TxtModDetail.Text = "Thunderstore unavailable: " + ex.Message;
        }
    }

    void OnModSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            FilterCatalog();
    }

    void OnModSearch(object sender, RoutedEventArgs e) => FilterCatalog();

    void FilterCatalog()
    {
        var q = TxtModSearch.Text.Trim();
        LstCatalog.Items.Clear();
        foreach (var p in _packages)
        {
            if (q.Length > 0 && p.FullName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && p.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            LstCatalog.Items.Add(new ListBoxItem
            {
                Content = $"{p.FullName}   {p.Versions.FirstOrDefault()?.VersionNumber}",
                Tag = p,
            });
        }
    }

    void OnCatalogSelected(object sender, SelectionChangedEventArgs e)
    {
        if ((LstCatalog.SelectedItem as ListBoxItem)?.Tag is ModPackage p)
            TxtModDetail.Text = p.Description;
    }

    bool CanChangeMods()
    {
        if (string.IsNullOrEmpty(_path) || !IsInstalled)
        {
            TxtModDetail.Text = "Install the server first.";
            return false;
        }
        if (_running.Values.Any(r => r.Supervisor.IsRunning) || DediProcesses.CountUnder(_path) > 0)
        {
            TxtModDetail.Text = "Stop the servers before changing mods.";
            return false;
        }
        return !_busy;
    }

    async Task RunModOp(string label, Func<HostMods, IProgress<ContentInstallProgress>, Task<string>> op)
    {
        if (!CanChangeMods())
            return;
        try
        {
            SetBusy(true, label);
            TxtModProgress.Text = label;
            TxtModProgressPct.Text = string.Empty;
            BarModProgress.IsIndeterminate = true;
            PanelModProgress.Visibility = Visibility.Visible;
            var last = DateTime.MinValue;
            var progress = new Progress<ContentInstallProgress>(p =>
            {
                var milestone = p.Phase is "check" or "commit";
                if (!milestone && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(100))
                    return;
                last = DateTime.UtcNow;
                ShowModProgress(p);
            });
            var path = _path;
            var result = await Task.Run(async () =>
            {
                using var mods = new HostMods(path);
                return await op(mods, progress).ConfigureAwait(false);
            }).ConfigureAwait(true);
            TxtModDetail.Text = result;
            ReloadCatalog();
            LoadInstanceForm(SelectedInstanceName());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TxtModDetail.Text = label + " failed: " + ex.Message;
        }
        finally
        {
            PanelModProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
            RefreshMods();
        }
    }

    void ShowModProgress(ContentInstallProgress p)
    {
        // Download is the long phase; extract/check/commit share the tail of each step.
        var frac = p.Phase switch
        {
            "download" when p.Total > 0 => 0.8 * p.Current / p.Total,
            "extract" when p.Total > 0 => 0.8 + 0.15 * p.Current / p.Total,
            "extract" => 0.8,
            "check" => 0.95,
            "commit" => 0.98,
            _ => 0.0,
        };
        var steps = Math.Max(1, p.StepCount);
        var pct = Math.Clamp(100.0 * (p.StepIndex + Math.Clamp(frac, 0, 1)) / steps, 0, 100);
        var indeterminate = p.Phase == "download" && p.Total <= 0;
        BarModProgress.IsIndeterminate = indeterminate;
        BarModProgress.Value = pct;
        BarProgress.Value = pct;
        TxtModProgressPct.Text = indeterminate ? string.Empty : pct.ToString("0") + "%";

        var verb = p.Phase switch
        {
            "download" => "Downloading",
            "extract" => "Extracting",
            "check" => "Checking",
            "commit" => "Installing",
            _ => p.Phase,
        };
        var text = verb + " " + p.Track;
        if (p.StepCount > 1)
            text += $"  ({p.StepIndex + 1}/{p.StepCount})";
        if (p.Unit == ProgressUnit.Bytes && p.Total > 0)
            text += $"  {FormatBytes(p.Current)} / {FormatBytes(p.Total)}";
        TxtModProgress.Text = text;
        SetStatus(text);
    }

    static string FormatBytes(long n) =>
        n >= 1L << 30 ? $"{n / (double)(1L << 30):0.00} GB"
        : n >= 1L << 20 ? $"{n / (double)(1L << 20):0.0} MB"
        : $"{n / 1024.0:0} KB";

    async void OnModInstall(object sender, RoutedEventArgs e)
    {
        if ((LstCatalog.SelectedItem as ListBoxItem)?.Tag is not ModPackage p)
            return;
        const bool required = true;
        await RunModOp("Installing " + p.FullName, async (mods, progress) =>
        {
            var plan = await mods.AddAsync(new[] { p.FullName }, required, null, CancellationToken.None, progress).ConfigureAwait(false);
            return "Installed " + string.Join(", ", plan.Select(s => s.Pin));
        });
    }

    async void OnModImport(object sender, RoutedEventArgs e)
    {
        var code = TxtProfileCode.Text.Trim();
        if (code.Length == 0)
            return;
        await RunModOp("Importing profile", async (mods, progress) =>
        {
            var plan = await mods.ImportProfileAsync(code, null, CancellationToken.None, progress).ConfigureAwait(false);
            return "Installed " + plan.Count + " mod(s).";
        });
    }

    async void OnModRemove(object sender, RoutedEventArgs e)
    {
        if ((LstInstalledMods.SelectedItem as ListBoxItem)?.Tag is not InstalledMod m)
            return;
        var key = string.IsNullOrEmpty(m.ThunderstoreFullName) ? m.FolderName : m.ThunderstoreFullName;
        await RunModOp("Removing " + key, (mods, _) =>
        {
            mods.Remove(key);
            return Task.FromResult("Removed " + key);
        });
    }

    async void OnModUpdateAll(object sender, RoutedEventArgs e) =>
        await RunModOp("Updating mods", async (mods, progress) =>
        {
            var moved = await mods.UpdateAllAsync(null, CancellationToken.None, progress).ConfigureAwait(false);
            return moved.Count == 0 ? "All mods are at their newest version." : "Updated " + string.Join(", ", moved);
        });

    async void OnModSync(object sender, RoutedEventArgs e) =>
        await RunModOp("Syncing mods", async (mods, progress) =>
        {
            await mods.SyncAsync(null, CancellationToken.None, progress).ConfigureAwait(false);
            return "Mods match mods.lock.json.";
        });

    // -------------------------------------------------------------- ui state

    void SetStatus(string text) => TxtStatus.Text = text;

    void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (status is not null)
            SetStatus(status);
        if (!busy)
            BarProgress.Value = 0;
        RefreshButtons();
    }

    void RefreshButtons()
    {
        var installed = IsInstalled;
        BtnInstall.IsEnabled = !_busy && !string.IsNullOrEmpty(_path);
        BtnInstall.Content = !installed ? "Install" : _lastCheck is { UpdateAvailable: true } ? "Update" : "Repair";
        BtnInstall.Style = (Style)FindResource(installed && _lastCheck is { UpdateAvailable: true } ? "UpdateButtonSmall" : "PrimaryButton");
        BtnCheck.IsEnabled = !_busy && !string.IsNullOrEmpty(_path);
        CmbRing.IsEnabled = !_busy;
        var sel = SelectedInstanceName();
        var running = sel is not null && IsRunningInstance(sel);
        BtnStart.IsEnabled = installed && !_busy && sel is not null && !running;
        BtnStop.IsEnabled = running;
        BtnRestart.IsEnabled = running;
        BtnStartAll.IsEnabled = installed && !_busy;
        BtnStopAll.IsEnabled = _running.Values.Any(r => r.Supervisor.IsRunning);
    }
}
