using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class MainWindow : Window
{
    public const string QuickPlayMap = SettingsStore.QuickPlayDefaultMap;

    /// <summary>Ready normally lands in ~10s; big maps stay under a minute.</summary>
    const int HostReadySpawnAnywaySeconds = 75;

    private HostReadyGate? _hostReady;
    private CancellationTokenSource? _playCts;

    private ChannelManifest? _manifest;
    private DownloadStatus _downloadGate = DownloadStatus.Unreachable;
    private DateTime _downloadGateUtc;
    private LauncherSettings _settings;
    private PlaylistCatalog _catalog = new();
    private int? _lastClientPid;
    private int? _lastDediPid;
    private bool _suppressArgsPersist;
    private bool _suppressDediUi;
    private bool _quickPlayBusy;
    private bool _installBusy;
    private bool _contentRepair;
    private CancellationTokenSource? _installCts;
    private InstallRunControl? _installRun;
    private bool _verifyBusy;
    private DateTime _progressMarkUtc;
    private long _progressMarkBytes;
    private string _progressMarkFile = "";
    private double _progressRate;
    private DateTime _progressLastUiUtc;
    private DispatcherTimer? _procWatch;
    private DispatcherTimer? _shellUpdateWatch;
    private readonly List<ModeCardViewModel> _modeCards = new();
    private ModeCardViewModel? _selectedMode;
    private SimplePlayKind _simplePlayKind = SimplePlayKind.Play;
    private LocalRconSession? _localRcon;
    private string? _livePlaylist;
    private string? _liveMap;
    private bool _hostedLocalMatch;
    private bool _hostedDediSeen;
    private bool _hostedClientSeen;
    private bool _clientGoneArmed;
    private bool _handlingServerCrash;
    private int _crashPromptPosted;
    private string? _pendingCrashExcerpt;
    private HostedServerFault _pendingFault;
    private string? _pendingClientMessage;
    private string? _lastScriptError;
    private DateTime _lastScriptErrorUtc;
    private DateTime _hostHeartbeatGraceUntilUtc = DateTime.MaxValue;
    // Each ping is a full RCON connect+auth the dedi logs, so it stays coarse.
    private static readonly TimeSpan HostHeartbeatEvery = TimeSpan.FromSeconds(10);
    private DateTime _hostHeartbeatNextUtc;
    private int _hostHeartbeatMisses;
    private DateTime _hostHeartbeatLastLogUtc;
    private const int HostHeartbeatMissLimit = 8;
    private static readonly TimeSpan HostHeartbeatGrace = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ScriptErrorShutdownWindow = TimeSpan.FromSeconds(20);

    private enum HostedServerFault
    {
        Crashed,
        Hung,
        ClientLost,
    }
    private bool _handlingClientGone;
    private bool _playLocalOwnsClient;
    private bool _changeMapBusy;
    private string? _pendingChangeMap;
    private string? _pendingChangeLabel;
    private bool _restartClientBusy;
    private bool _dx12Available;

    // A client this launcher started is the only one it knows how to put back:
    // it holds the args, the connect target and the console tap. Kept off PIDs
    // because the loader relaunches the image and the PID changes under us.
    private bool _launcherOwnsClient;
    private bool _installDiskBlocked;
    private string _installDiskMessage = "";
    private bool _windowClosing;
    private bool _suppressLang;
    private ShellSelfUpdate.State _shellUpdateState;
    private string? _shellUpdateVer;

    // Content health walks every manifest entry (stat per file), so it never
    // runs inline on the UI thread. Painters only read the memo; KickHealthRefresh
    // is for startup, path change, install, and focus — not the 1.5s watchdog.
    private InstallHealthReport? _healthMemo;
    private DateTime _healthMemoUtc;
    private string _healthMemoRoot = "";
    private int _healthRefreshBusy;
    private const double HealthMemoTtlSeconds = 120;
    private int _procWatchBusy;
    private bool? _watchClientAlive;
    private bool? _watchDediAlive;
    private bool? _watchInstallPresent;
    private string _lastPlayCaption = "";
    private bool _lastPlayEnabled;
    private bool _lastPlayStop;
    private SimplePlayKind _lastPaintKind;
    private bool _playPainted;

    private bool HealthMemoFresh(string root) =>
        _healthMemo is not null &&
        string.Equals(_healthMemoRoot, root, StringComparison.OrdinalIgnoreCase) &&
        (DateTime.UtcNow - _healthMemoUtc).TotalSeconds < HealthMemoTtlSeconds;

    /// <summary>Paint from the memo. Missing/wrong-root kicks one background walk.</summary>
    private InstallHealthReport HealthForUi(string root)
    {
        var memo = _healthMemo;
        if (memo is not null &&
            string.Equals(_healthMemoRoot, root, StringComparison.OrdinalIgnoreCase))
            return memo;

        KickHealthRefresh(root);
        return memo ?? new InstallHealthReport { InstallPath = root };
    }

    private void KickHealthRefresh(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;
        if (Interlocked.CompareExchange(ref _healthRefreshBusy, 1, 0) != 0)
            return;
        var manifest = _manifest;
        _ = Task.Run(() =>
        {
            try
            {
                var report = ContentInstallService.Assess(manifest, root, true, true);
                // Root first: readers pair the memo with this field, so publishing
                // the report ahead of it can hand a painter the wrong pairing.
                _healthMemoRoot = root;
                _healthMemoUtc = DateTime.UtcNow;
                _healthMemo = report;
            }
            catch { /* memo just stays stale */ }
            finally
            {
                Interlocked.Exchange(ref _healthRefreshBusy, 0);
            }

            if (!_windowClosing)
                Dispatcher.BeginInvoke(() =>
                {
                    RefreshInstallStateLabels();
                    RefreshSimplePlayButton();
                });
        });
    }

    private void InvalidateHealthMemo()
    {
        _healthMemo = null;
        _healthMemoUtc = DateTime.MinValue;
    }

    private enum SimplePlayKind
    {
        Play,
        Stop,
        ChangeMap,
        RestartClient,
        Disconnect,
        Install,
        Repair,
        Update,
        SetUp,
        SwitchAdvanced,
        Installing,
    }

    /// <summary>A game session is up, whatever the button currently offers to do about it.</summary>
    private bool SessionLive => _simplePlayKind
        is SimplePlayKind.Stop or SimplePlayKind.ChangeMap
        or SimplePlayKind.RestartClient or SimplePlayKind.Disconnect;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            Loaded += OnWindowLoaded;
            SourceInitialized += OnSourceInitialized;
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;
            StateChanged += (_, _) => ApplyWindowStateChrome();
            Activated += OnWindowActivated;

            _settings = SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(_settings.UiLanguage))
            {
                _settings.UiLanguage = Loc.Code;
                _settings.EulaLanguage = Loc.Code;
                try { SettingsStore.Save(_settings); }
                catch { }
            }
            Loc.LanguageChanged += OnLocLanguageChanged;

            if (Application.Current is App app &&
                !string.IsNullOrWhiteSpace(app.CliInstallRoot) &&
                !InstallPathPolicy.IsForbidden(app.CliInstallRoot, AppContext.BaseDirectory))
            {
                _settings.InstallPath = app.CliInstallRoot!;
            }

            // Paint UI immediately; heavy playlist/loc load runs after Loaded.
            if (ListCredits is not null)
                ListCredits.ItemsSource = CreditsCatalog.All;
            BindNotesLocal();
            BindLauncherVersion();

            _suppressArgsPersist = true;
            TxtInstallRoot.Text = _settings.InstallPath;
            if (TxtSimpleInstallRoot is not null)
                TxtSimpleInstallRoot.Text = _settings.InstallPath;
            TxtClientArgs.Text = _settings.ClientLaunchArguments;
            TxtDediArgs.Text = _settings.DediLaunchArguments;
            // Stored pairs from before the two toggles were made exclusive can carry
            // both; hosting is the deliberate opt-in, so it keeps the conflict.
            if (_settings.DediHostOnline && _settings.OfflineNoAuth)
                _settings.OfflineNoAuth = false;
            if (ChkSimpleOffline is not null)
                ChkSimpleOffline.IsChecked = _settings.OfflineNoAuth;
            if (ChkDediOnline is not null)
                ChkDediOnline.IsChecked = _settings.DediHostOnline;
            SyncDeveloperChecks();
            if (ChkSimpleCheats is not null)
                ChkSimpleCheats.IsChecked = _settings.Cheats;
            ChkFilterMaps.IsChecked = _settings.FilterMapsByPlaylist;
            if (ChkOpenConsoleOnLaunch is not null)
                ChkOpenConsoleOnLaunch.IsChecked = _settings.OpenConsoleOnLaunch;
            if (ChkShowUnlistedMaps is not null)
                ChkShowUnlistedMaps.IsChecked = _settings.ShowUnlistedMaps;
            if (ChkUseDx12 is not null)
                ChkUseDx12.IsChecked = _settings.UseDx12;
            if (ChkClientDx12 is not null)
                ChkClientDx12.IsChecked = _settings.UseDx12;
            if (ChkServersDx12 is not null)
                ChkServersDx12.IsChecked = _settings.UseDx12;
            BuildResolutionPresetMenu();
            ApplyResolutionFields();
            InitDownloadLimit();
            InitLanguageCombos();
            TxtDediPort.Text = _settings.DediPort > 0
                ? _settings.DediPort.ToString()
                : LaunchArgs.DefaultDediPort.ToString();
            SetPasswordBoxes(_settings.DediPassword);
            SetPasswordProtectChecked(_settings.DediPasswordEnabled);
            ApplyPasswordProtectVisibility();
            _suppressArgsPersist = false;

            ApplyShellMode(simple: _settings.SimpleMode, persist: false);

            TxtStatus.Text = Loc.Get("loading");
            TxtLog.Text = $"[{DateTime.Now:HH:mm:ss}] R5Flowstate Shell — LOCAL testing only.{Environment.NewLine}";
            if (Application.Current is App a && !string.IsNullOrWhiteSpace(a.CliInstallRoot))
                Log($"CLI --install-root: {a.CliInstallRoot}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Format("msg_window_failed", ex),
                Loc.Get("title_app"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource src)
        {
            if (_settings.ForceSoftwareRender ||
                (RenderCapability.Tier >> 16) == 0)
            {
                src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            }
            src.AddHook(WndProc);
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { }
    }

    void OnCaptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* released before drag */ }
        }
    }

    void OnWinMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    void OnWinMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    void OnWinClose(object sender, RoutedEventArgs e) => Close();

    void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    void ApplyWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Borderless maximize overhangs by the resize border; pad it back in.
        if (RootChrome is not null)
            RootChrome.Margin = maximized ? new Thickness(7) : new Thickness(0);
        if (TxtWinMaxGlyph is not null)
            TxtWinMaxGlyph.Text = maximized ? "\uE923" : "\uE922";
        if (BtnWinMaximize is not null)
            BtnWinMaximize.ToolTip = Loc.Get(maximized ? "restore" : "maximize");
    }

    void OnLocLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedChrome();
        _blogBodies.Clear();
        _ = RefreshBlogAsync();
    }

    void InitLanguageCombos()
    {
        var picker = NoticeLanguages.Picker;
        var current = NoticeLanguages.ForUi(Loc.Code);
        NoticeLanguage? selected = null;
        foreach (var row in picker)
        {
            if (string.Equals(row.Code, current, StringComparison.Ordinal))
            {
                selected = row;
                break;
            }
        }
        selected ??= picker[0];

        _suppressLang = true;
        try
        {
            if (CmbUiLanguage is not null)
            {
                CmbUiLanguage.ItemsSource = picker;
                CmbUiLanguage.SelectedItem = selected;
            }
            if (CmbUiLanguageSetup is not null)
            {
                CmbUiLanguageSetup.ItemsSource = picker;
                CmbUiLanguageSetup.SelectedItem = selected;
            }
        }
        finally
        {
            _suppressLang = false;
        }
    }

    void OnUiLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressLang)
            return;
        if (sender is not ComboBox box || box.SelectedItem is not NoticeLanguage row)
            return;
        ApplyUiLanguage(row.Code);
    }

    void ApplyUiLanguage(string code)
    {
        var canon = NoticeLanguages.ForUi(code);
        if (string.Equals(canon, Loc.Code, StringComparison.Ordinal))
        {
            InitLanguageCombos();
            return;
        }

        Loc.SetLanguage(canon);
        _settings.UiLanguage = canon;
        _settings.EulaLanguage = canon;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        InitLanguageCombos();
    }

    void RefreshLocalizedChrome()
    {
        InitLanguageCombos();
        _suppressArgsPersist = true;
        try { InitDownloadLimit(); }
        finally { _suppressArgsPersist = false; }
        BuildResolutionPresetMenu();
        PaintInstallControls();
        RefreshSimplePlayButton();
        RefreshSimpleInstallCopy();
        RefreshDx12Chrome();
        RefreshChangeMapButton();
        RefreshHeaderSubtitle();
        ApplyNotesPage();
        ApplyWindowStateChrome();
        SetShellUpdateChrome(_shellUpdateState, _shellUpdateVer);
        if (BtnModeToggle is not null)
            BtnModeToggle.ToolTip = Loc.Get(_settings.SimpleMode ? "tip_mode_advanced" : "tip_mode_simple");
        ShowSimpleSetupLayout(PanelSimpleSetup?.Visibility == Visibility.Visible);
        ApplySimpleTab(_simpleTab);
        RefreshConnectionChrome();
        RefreshInstallStateLabels();
        ReloadPlaylistsAndMaps(selectSaved: true);
        RelabelServerRows();
        if (_simplePlayKind is SimplePlayKind.Play || SessionLive)
        {
            if (_simplePlayKind == SimplePlayKind.Play)
                SetSimpleStatus(Loc.Get("ready_to_play"));
        }
    }

    static string ModeTitle(string playlistId, string fallback) =>
        ModeCardViewModel.LocalizedTitle(playlistId, fallback);

    static string ModeBlurb(string playlistId, string fallback) =>
        ModeCardViewModel.LocalizedBlurb(playlistId, fallback);

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Activate();
            Topmost = true;
            Topmost = false;

            var orphan7z = SevenZipLocator.KillOurUnpackers();
            if (orphan7z > 0)
                Log($"Killed {orphan7z} leftover 7-Zip unpacker(s).");
            ConsumePendingJoin();
            KickShellSelfUpdate();
            _shellUpdateWatch = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _shellUpdateWatch.Tick += (_, _) =>
            {
                KickShellSelfUpdate();
                _ = RecheckChannelAsync();
                _ = RefreshBlogAsync();
            };
            _shellUpdateWatch.Start();
            ReloadPlaylistsAndMaps(selectSaved: true);
            InitServerBrowser();
            BindConsoleTaps();
            RefreshDx12Chrome();
            _ = RefreshBlogAsync();
            Log($"Install directory: {TxtInstallRoot.Text}");
            RefreshArgPreviews();
            RefreshInstallStateLabels();
            UpdateKillButtons();

            _procWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _procWatch.Tick += (_, _) =>
            {
                TickProcessWatch();
                TickHostHeartbeat();
            };
            _procWatch.Start();

            UpdateStatus(Loc.Get("status_checking"));
            SetSimpleStatus(Loc.Get("status_checking"));
            _ = StartupChannelFlowAsync();

            Dispatcher.BeginInvoke(() => TrimWorkingSet(), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            Log("Startup load failed: " + ex.Message);
            UpdateStatus("ERROR: " + ex.Message);
            MessageBox.Show(this, Loc.Format("msg_startup_failed", ex.Message), Loc.Get("title_app"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool? _installPresent;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (!IsLoaded || _windowClosing)
            return;
        var root = ReadInstallPathBox();
        if (!HealthMemoFresh(root))
            KickHealthRefresh(root);
    }

    private void TickProcessWatch()
    {
        if (_windowClosing)
            return;
        if (Interlocked.CompareExchange(ref _procWatchBusy, 1, 0) != 0)
            return;

        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        _ = Task.Run(() =>
        {
            try
            {
                var roles = ProcessSpawner.PeekRoles(root);
                var present = !NeedsSetup(root);
                if (_windowClosing)
                {
                    Interlocked.Exchange(ref _procWatchBusy, 0);
                    return;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        ApplyProcessWatch(roles.client, roles.dedi, present);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _procWatchBusy, 0);
                    }
                });
            }
            catch
            {
                Interlocked.Exchange(ref _procWatchBusy, 0);
            }
        });
    }

    private void ApplyProcessWatch(bool clientAlive, bool dediAlive, bool present)
    {
        if (_windowClosing)
            return;

        WatchHostedMatch(dediAlive);
        WatchHostedClientGone(clientAlive, dediAlive);
        WatchSessionModsClient(clientAlive);
        WatchInstallPresence(present);

        var unchanged = _watchClientAlive == clientAlive
            && _watchDediAlive == dediAlive
            && _watchInstallPresent == present
            && !_quickPlayBusy && !_installBusy && !_verifyBusy && !_joinBusy
            && !_restartClientBusy;
        _watchClientAlive = clientAlive;
        _watchDediAlive = dediAlive;
        _watchInstallPresent = present;
        if (unchanged)
            return;

        UpdateKillButtons(clientAlive, dediAlive);
    }

    /// <summary>
    /// The game folder can be deleted from Explorer while the launcher is open.
    /// Nothing else notices, so PLAY would keep pointing at files that are gone.
    /// </summary>
    private void WatchInstallPresence() =>
        WatchInstallPresence(!NeedsSetup(ReadInstallPathBox()));

    private void WatchInstallPresence(bool present)
    {
        if (_installBusy || _verifyBusy)
            return;

        if (_installPresent == present)
            return;

        _installPresent = present;
        if (!IsLoaded)
            return;

        Log(present ? "Install folder is present again." : "Install folder went missing.");
        InvalidateHealthMemo();
        if (!present)
            ShowSimpleSetupLayout(true);
        RefreshInstallStateLabels();
        ApplyInstallGates();
    }

    /// <summary>
    /// A folder change is the one action that can lose an install the launcher
    /// holds no other record of, so the outgoing path is kept whenever it still
    /// has a game in it. <see cref="RecoverInstallPathIfStranded"/> reads it back.
    /// </summary>
    void RememberOutgoingInstallFolder(string incoming)
    {
        var outgoing = _settings.InstallPath;
        if (string.IsNullOrWhiteSpace(outgoing) ||
            string.Equals(outgoing, incoming, StringComparison.OrdinalIgnoreCase) ||
            NeedsSetup(outgoing))
            return;

        _settings.PreviousInstallPath = outgoing;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }

    /// <summary>
    /// Startup guard: no game at the saved path means the last folder change
    /// landed somewhere empty, so fall back to the folder that had one instead
    /// of opening first-run setup over a live install.
    /// </summary>
    bool RecoverInstallPathIfStranded()
    {
        var previous = _settings.PreviousInstallPath;
        if (string.IsNullOrWhiteSpace(previous) ||
            string.Equals(previous, _settings.InstallPath, StringComparison.OrdinalIgnoreCase) ||
            NeedsSetup(previous))
            return false;

        Log($"Install path '{_settings.InstallPath}' has no game - restoring '{previous}'.");
        _settings.InstallPath = previous;
        _settings.PreviousInstallPath = string.Empty;
        _settings.InitialInstallAccepted = true;
        SyncInstallPathBoxes(previous);
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        return true;
    }

    private static bool NeedsSetup(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return true;
        return !ProcessSpawner.Preflight(installPath, LaunchRole.Client).Ok;
    }

    /// <summary>
    /// Startup sequence that used to run inline in Loaded: channel first (the
    /// auto-update assess and the install adopt both read _manifest), then the
    /// install-folder check. All network waits happen off the UI thread.
    /// </summary>
    private async Task StartupChannelFlowAsync()
    {
        await LoadChannelAsync().ConfigureAwait(true);
        await RefreshDownloadGateAsync().ConfigureAwait(true);
        await MaybeAutoApplySmallUpdateAsync().ConfigureAwait(true);

        if (NeedsSetup(_settings.InstallPath) && !RecoverInstallPathIfStranded())
        {
            Log("Install directory missing client triad (r5apex.exe / loader.dll / client.dll). Set path above.");
            UpdateStatus(Loc.Get("status_set_dir"));
            SetSimpleStatus(Loc.Get("status_pick_folder_install"));
            ApplyInstallDiskStatus();
            // Without this the big button keeps whatever caption it was last
            // painted with, which reads as PLAY on a folder that is gone.
            InvalidateHealthMemo();
            ShowSimpleSetupLayout(true);
            RefreshSimplePlayButton();
        }
        else
        {
            _ = AdoptInstallFolderAsync(_settings.InstallPath, persist: false);
        }
    }

    bool ChannelIsLocal()
    {
        var configured = Environment.GetEnvironmentVariable(ChannelSource.ChannelUrlEnvVar)
                         ?? _settings.ChannelUrl;
        if (string.IsNullOrWhiteSpace(configured))
            configured = ProductConstants.DefaultChannelUrl;
        return !string.IsNullOrWhiteSpace(configured) && ChannelSource.LooksLocal(configured);
    }

    async Task<DownloadStatus> RefreshDownloadGateAsync()
    {
        if (ChannelIsLocal())
        {
            _downloadGate = DownloadStatus.AllowAll;
            _downloadGateUtc = DateTime.UtcNow;
            return _downloadGate;
        }

        try
        {
            _downloadGate = await MasterServerClient.GetDownloadStatusAsync(
                    ProductConstants.DefaultMasterServerUrl)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("Download gate fetch failed (play allowed): " + ex.Message);
            _downloadGate = DownloadStatus.Unreachable;
        }

        _downloadGateUtc = DateTime.UtcNow;
        Log($"Download gate: reachable={_downloadGate.Reachable} setup={_downloadGate.Setup} content={_downloadGate.Content} platform={_downloadGate.Platform} dedi={_downloadGate.Dedi}");
        ApplyDediPackageVisibility();
        return _downloadGate;
    }

    DownloadStatus CachedDownloadGate() =>
        ChannelIsLocal() ? DownloadStatus.AllowAll : _downloadGate;

    static bool LaneBlocksPlay(InstallHealthReport health, DownloadStatus gate, bool requireClient, bool requireServer)
    {
        if (!gate.Reachable)
            return false;
        if (gate.Content && requireClient &&
            health.Client?.Status == InstallHealthStatus.UpdateAvailable)
            return true;
        if (gate.Content && requireServer &&
            health.Server?.Status == InstallHealthStatus.UpdateAvailable)
            return true;
        if (gate.Platform &&
            health.Platform?.Status == InstallHealthStatus.UpdateAvailable)
            return true;
        return false;
    }

    string ChannelCacheDir() =>
        Path.Combine(
            string.IsNullOrWhiteSpace(TxtInstallRoot.Text)
                ? Path.GetTempPath()
                : TxtInstallRoot.Text.Trim(),
            ProductConstants.ContentCacheDirName,
            "channel");

    /// <summary>
    /// Synchronous, disk-only manifest recovery for paths that cannot await
    /// (play gates). Loads the last fetched cache copy; never touches the
    /// network. Gates fail open when no manifest is available.
    /// </summary>
    private bool TryLoadChannelFromCache()
    {
        if (_manifest is not null)
            return true;
        try
        {
            var cached = ChannelSource.CachedManifestPath(ChannelCacheDir());
            if (!File.Exists(cached))
                return false;
            _manifest = ChannelManifestIO.Load(cached);
            InvalidateHealthMemo();
            Log("Channel loaded from cache: " + cached);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> LoadChannelAsync()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable(ChannelSource.ChannelUrlEnvVar)
                             ?? _settings.ChannelUrl;
            if (string.IsNullOrWhiteSpace(configured))
                configured = ProductConstants.DefaultChannelUrl;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    var url = configured;
                    var cache = ChannelCacheDir();
                    var manifest = await Task.Run(() =>
                    {
                        using var fetcher = new FileSystemFetcher(TimeSpan.FromSeconds(15));
                        return ChannelSource.LoadAsync(url, fetcher, cache)
                            .GetAwaiter().GetResult();
                    }).ConfigureAwait(true);
                    _manifest = manifest;
                    InvalidateHealthMemo();
                    var remoteGate = _manifest.EffectiveGateName;
                    TxtIdentity.Text =
                        $"gate={remoteGate}  client={_manifest.Client?.CatalogVersion ?? "-"}  " +
                        $"server={_manifest.Server?.CatalogVersion ?? "-"}  " +
                        $"platform={_manifest.Platform?.CatalogVersion ?? "-"}  url={configured}";
                    Log("Channel loaded: " + configured);
                    _ = RefreshNotesAsync(configured);
                    return true;
                }
                catch (Exception ex)
                {
                    Log("Channel URL failed (" + configured + "): " + ex.Message);
                }
            }

            if (TryLoadChannelFromCache())
            {
                var cachedGate = _manifest!.EffectiveGateName;
                TxtIdentity.Text =
                    $"gate={cachedGate}  client={_manifest.Client?.CatalogVersion ?? "-"}  " +
                    $"server={_manifest.Server?.CatalogVersion ?? "-"}  " +
                    $"platform={_manifest.Platform?.CatalogVersion ?? "-"}  url={configured} (cache)";
                Log("Channel loaded from cache: " + configured);
                BindNotesLocal();
                return true;
            }

            var path = ResolveChannelPath();
            if (!File.Exists(path))
            {
                TxtIdentity.Text = "Channel fixture optional (tools/local_channel or content_fixtures).";
                _manifest = null;
                BindNotesLocal();
                return false;
            }

            _manifest = ChannelManifestIO.Load(path);
            var gate = _manifest.EffectiveGateName;
            TxtIdentity.Text =
                $"gate={gate}  client={_manifest.Client?.CatalogVersion ?? "-"}  " +
                $"server={_manifest.Server?.CatalogVersion ?? "-"}  " +
                $"file={path}";
            Log($"Channel loaded: {path}");
            _ = RefreshNotesAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            if (TryLoadChannelFromCache())
            {
                Log("Channel loaded from cache after error: " + ex.Message);
                BindNotesLocal();
                return true;
            }
            _manifest = null;
            TxtIdentity.Text = "Channel: " + ex.Message;
            BindNotesLocal();
            return false;
        }
    }

    /// <summary>
    /// Background channel re-fetch so an open launcher notices new game /
    /// platform tips without a restart. Re-assesses only on a tip change.
    /// </summary>
    async Task RecheckChannelAsync()
    {
        if (_installBusy || _verifyBusy)
            return;

        var configured = Environment.GetEnvironmentVariable(ChannelSource.ChannelUrlEnvVar)
                         ?? _settings.ChannelUrl;
        if (string.IsNullOrWhiteSpace(configured))
            configured = ProductConstants.DefaultChannelUrl;
        if (string.IsNullOrWhiteSpace(configured))
            return;

        ChannelManifest manifest;
        try
        {
            var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
            var cache = Path.Combine(
                string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root,
                ProductConstants.ContentCacheDirName,
                "channel");
            var url = configured;
            manifest = await Task.Run(() =>
            {
                using var fetcher = new FileSystemFetcher(TimeSpan.FromSeconds(15));
                return ChannelSource.LoadAsync(url, fetcher, cache)
                    .GetAwaiter().GetResult();
            }).ConfigureAwait(true);
        }
        catch
        {
            await RefreshDownloadGateAsync().ConfigureAwait(true);
            RefreshSimplePlayButton();
            return;
        }

        var changed = _manifest is null
            || !string.Equals(manifest.Client?.CatalogVersion, _manifest.Client?.CatalogVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.Server?.CatalogVersion, _manifest.Server?.CatalogVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.Platform?.CatalogVersion, _manifest.Platform?.CatalogVersion, StringComparison.Ordinal);

        await RefreshDownloadGateAsync().ConfigureAwait(true);
        if (!changed)
        {
            RefreshSimplePlayButton();
            return;
        }

        _manifest = manifest;
        InvalidateHealthMemo();
        Log("Channel changed: " +
            $"client={manifest.Client?.CatalogVersion ?? "-"} " +
            $"server={manifest.Server?.CatalogVersion ?? "-"} " +
            $"platform={manifest.Platform?.CatalogVersion ?? "-"}");
        _ = RefreshNotesAsync(configured);

        if (_installBusy || _verifyBusy)
            return;
        _ = AdoptInstallFolderAsync(_settings.InstallPath, persist: false);
    }

    void BindLauncherVersion()
    {
        var label = "v" + LauncherVersion.Display;
        if (TxtSimpleVersion is not null)
            TxtSimpleVersion.Text = label;
        if (TxtAdvancedVersion is not null)
            TxtAdvancedVersion.Text = label;
    }

    List<PatchNoteEntry> _notesGame = new();
    List<PatchNoteEntry> _notesLauncher = new();
    bool _notesShowLauncher;

    string NotesCacheDir() =>
        Path.Combine(
            string.IsNullOrWhiteSpace(TxtInstallRoot?.Text)
                ? Path.GetTempPath()
                : TxtInstallRoot.Text.Trim(),
            ProductConstants.ContentCacheDirName,
            "notes");

    /// <summary>Bind last-fetched / bundled notes from disk. Never network.</summary>
    private void BindNotesLocal()
    {
        if (ListNotes is null)
            return;

        var game = new List<PatchNoteEntry>();
        var launcher = new List<PatchNoteEntry>();
        try
        {
            var cache = NotesCacheDir();
            game.AddRange(ToPatchNotes(NotesSource.LoadGameCachedOrBundled(cache)));
            launcher.AddRange(ToPatchNotes(NotesSource.LoadLauncherCachedOrBundled(cache)));
        }
        catch (Exception ex)
        {
            Log("Notes load failed: " + ex.Message);
        }

        if (game.Count == 0)
            game.AddRange(PatchNotesCatalog.All);

        _notesGame = game;
        _notesLauncher = launcher;
        PreferLauncherNotesIfCurrentUnseen();
        ApplyNotesPage();
    }

    /// <summary>Fetch fresh notes off the UI thread and rebind on arrival.</summary>
    private async Task RefreshNotesAsync(string? channelUrl)
    {
        if (ListNotes is null)
            return;

        var manifest = _manifest;
        var cache = NotesCacheDir();
        List<NotesEntry> gameRows;
        List<NotesEntry> launcherRows;
        try
        {
            (gameRows, launcherRows) = await Task.Run(() =>
            {
                using var fetcher = new FileSystemFetcher(TimeSpan.FromSeconds(15));
                var g = NotesSource.LoadAsync(manifest, channelUrl, fetcher, cache)
                    .GetAwaiter().GetResult();
                var l = NotesSource.LoadLauncherAsync(fetcher, cache)
                    .GetAwaiter().GetResult();
                return (g, l);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("Notes refresh failed: " + ex.Message);
            return;
        }

        var game = new List<PatchNoteEntry>(ToPatchNotes(gameRows));
        if (game.Count == 0)
            game.AddRange(PatchNotesCatalog.All);

        _notesGame = game;
        _notesLauncher = new List<PatchNoteEntry>(ToPatchNotes(launcherRows));
        PreferLauncherNotesIfCurrentUnseen();
        ApplyNotesPage();
    }

    void PreferLauncherNotesIfCurrentUnseen()
    {
        var needle = "Launcher " + LauncherVersion.Display;
        if (_notesLauncher.Count == 0)
            return;
        if (_notesLauncher.All(e =>
                e.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0))
            return;
        if ((_settings.NotesSeenStamp ?? string.Empty)
                .IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            return;
        _notesShowLauncher = true;
    }

    static IEnumerable<PatchNoteEntry> ToPatchNotes(List<NotesEntry> entries) =>
        entries.Select(e => new PatchNoteEntry
        {
            Date = string.IsNullOrWhiteSpace(e.Date) ? "—" : e.Date,
            Title = string.IsNullOrWhiteSpace(e.Title) ? "Notes" : e.Title,
            Items = (e.Items ?? []).Select(PatchNoteLine.Parse).ToList(),
        });

    void OnNotesPageGame(object sender, RoutedEventArgs e)
    {
        _notesShowLauncher = false;
        ApplyNotesPage();
    }

    void OnNotesPageLauncher(object sender, RoutedEventArgs e)
    {
        _notesShowLauncher = true;
        ApplyNotesPage();
    }

    void ApplyNotesPage()
    {
        if (ListNotes is null)
            return;
        ListNotes.ItemsSource = _notesShowLauncher ? _notesLauncher : _notesGame;
        ScrollNotes?.ScrollToTop();
        StyleTab(BtnNotesGame, !_notesShowLauncher);
        StyleTab(BtnNotesLauncher, _notesShowLauncher);
        SyncNotesUnreadDot(markSeen: _simpleTab == SimpleTab.Notes);
    }

    static string NotesStamp(
        IReadOnlyList<PatchNoteEntry> game,
        IReadOnlyList<PatchNoteEntry> launcher)
    {
        var sb = new StringBuilder();
        AppendNotesStamp(sb, "g", game);
        AppendNotesStamp(sb, "l", launcher);
        return sb.ToString();
    }

    static void AppendNotesStamp(
        StringBuilder sb,
        string tag,
        IReadOnlyList<PatchNoteEntry> rows)
    {
        foreach (var e in rows)
        {
            sb.Append(tag);
            sb.Append('\t');
            sb.Append(e.Date);
            sb.Append('\t');
            sb.Append(e.Title);
            sb.Append('\n');
        }
    }

    void SyncNotesUnreadDot(bool markSeen)
    {
        var stamp = NotesStamp(_notesGame, _notesLauncher);
        if (markSeen &&
            !string.Equals(_settings.NotesSeenStamp, stamp, StringComparison.Ordinal))
        {
            _settings.NotesSeenStamp = stamp;
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log("Settings save failed: " + ex.Message); }
        }

        var unread = stamp.Length > 0 &&
            !string.Equals(_settings.NotesSeenStamp, stamp, StringComparison.Ordinal);
        if (DotNotesUnread is not null)
            DotNotesUnread.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Prefer tools/local_channel (master s21-full tips) for normal play.
    /// Synthetic content_fixtures CHANNEL is pack-smoke only — never override local.
    /// </summary>
    private static string ResolveChannelPath()
    {
        var local = ResolveLocalChannelPath();
        if (File.Exists(local))
            return local;

        foreach (var repo in EnumerateRepoRoots())
        {
            var fixture = Path.Combine(repo, "tools", "content_fixtures", "CHANNEL_MANIFEST.json");
            if (File.Exists(fixture))
                return fixture;
        }

        return local;
    }

    private static string ResolveLocalChannelPath()
    {
        foreach (var repo in EnumerateRepoRoots())
        {
            var dir = Path.Combine(repo, "tools", "local_channel");
            foreach (var name in new[] { "CHANNEL_MANIFEST.local.json", "CHANNEL_MANIFEST.json" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                    return p;
            }
        }

        return Path.Combine("tools", "local_channel", "CHANNEL_MANIFEST.local.json");
    }

    private static IEnumerable<string> EnumerateRepoRoots()
    {
        var fromOutput = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var fromCwd = Path.GetFullPath(Environment.CurrentDirectory);
        foreach (var root in new[] { fromOutput, fromCwd })
        {
            if (Directory.Exists(root))
                yield return root;
        }
    }

    private void ReloadPlaylistsAndMaps(bool selectSaved)
    {
        var root = TxtInstallRoot.Text.Trim();
        try
        {
            _catalog = PlaylistCatalogLoader.Load(root, Loc.Code, _settings.ShowUnlistedMaps, Loc.Lookup);
        }
        catch (Exception ex)
        {
            Log($"Playlist load failed: {ex.Message}");
            _catalog = new PlaylistCatalog();
        }

        _suppressDediUi = true;
        try
        {
            CmbPlaylist.ItemsSource = _catalog.HostPlaylists;
            FillMapCombo(selectSaved ? _settings.DediMap : SelectedMap());

            if (selectSaved)
            {
                SelectPlaylist(_settings.DediPlaylist);
                SelectMap(_settings.DediMap);
            }

            if (CmbPlaylist.SelectedItem is null && _catalog.HostPlaylists.Count > 0)
            {
                SelectPlaylist("survival_dev");
                if (CmbPlaylist.SelectedItem is null)
                    CmbPlaylist.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressDediUi = false;
        }

        UpdatePlaylistDetail();
        RebuildModeCards();
        var src = _catalog.SourcePath is not null
            ? Path.GetFileName(_catalog.SourcePath)
            : "(no playlist file)";
        var loc = _catalog.LocalizationPath is not null
            ? Path.GetFileName(_catalog.LocalizationPath)
            : "no loc";
        TxtPlaylistSource.Text =
            $"{src}  |  {_catalog.HostPlaylists.Count} playlists  |  {_catalog.AllMaps.Count} maps  |  {loc}";
        Log($"Playlists reloaded: {src}, host={_catalog.HostPlaylists.Count}, file={_catalog.Entries.Count}, maps={_catalog.AllMaps.Count}");
        RefreshArgPreviews();
        RefreshSimplePlayButton();
    }

    private void FillMapCombo(string? prefer)
    {
        IReadOnlyList<string> maps;
        if (ChkFilterMaps.IsChecked == true)
            maps = _catalog.MapsForPlaylist(SelectedPlaylistId());
        else
            maps = _catalog.AllMaps;

        CmbMap.ItemsSource = maps;
        if (!string.IsNullOrWhiteSpace(prefer) && maps.Any(m =>
                string.Equals(m, prefer, StringComparison.OrdinalIgnoreCase)))
            SelectMap(prefer);
        else if (CmbMap.Items.Count > 0 && CmbMap.SelectedIndex < 0)
            CmbMap.SelectedIndex = 0;
    }

    private void SelectPlaylist(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        foreach (var item in CmbPlaylist.Items)
        {
            if (item is PlaylistEntry e &&
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                CmbPlaylist.SelectedItem = e;
                return;
            }
        }
    }

    private void SelectMap(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        foreach (var item in CmbMap.Items)
        {
            if (item is string s && string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
            {
                CmbMap.SelectedItem = s;
                CmbMap.Text = s;
                return;
            }
        }
        CmbMap.Text = value;
    }

    private string SelectedPlaylistId()
    {
        if (CmbPlaylist.SelectedItem is PlaylistEntry e)
            return e.Id;
        return string.Empty;
    }

    private string SelectedMap()
    {
        if (CmbMap.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
            return s;
        return (CmbMap.Text ?? string.Empty).Trim();
    }

    private void UpdatePlaylistDetail()
    {
        if (TxtPlaylistDetail is null)
            return;
        if (CmbPlaylist.SelectedItem is PlaylistEntry e)
        {
            var key = string.IsNullOrWhiteSpace(e.NameKey) ? "-" : e.NameKey;
            TxtPlaylistDetail.Text =
                $"+launchplaylist {e.Id}   |   name={key}   |   maps in def={e.Maps.Count}";
        }
        else
        {
            TxtPlaylistDetail.Text = string.Empty;
        }
    }

    private int SelectedPort()
    {
        if (int.TryParse(TxtDediPort.Text?.Trim(), out var p) && p > 0 && p < 65536)
            return p;
        return LaunchArgs.DefaultDediPort;
    }


    private void OnReloadPlaylists(object sender, RoutedEventArgs e) =>
        ReloadPlaylistsAndMaps(selectSaved: false);

    private void OnFilterMapsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist || _suppressDediUi)
            return;
        var keep = SelectedMap();
        FillMapCombo(keep);
        PersistSettingsFromUi();
        RefreshArgPreviews();
    }

    private void OnShowUnlistedMapsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        _settings.ShowUnlistedMaps = ChkShowUnlistedMaps?.IsChecked == true;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        ReloadPlaylistsAndMaps(selectSaved: true);
    }

    private void OnDediSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressDediUi || _suppressArgsPersist)
            return;

        if (ReferenceEquals(sender, CmbPlaylist))
        {
            UpdatePlaylistDetail();
            if (ChkFilterMaps.IsChecked == true)
                FillMapCombo(SelectedMap());
        }

        PersistSettingsFromUi();
        RefreshArgPreviews();
        RefreshChangeMapButton();
    }

    private void OnBrowseInstall(object sender, RoutedEventArgs e)
    {
        var dir = PickInstallFolder();
        if (dir is null)
            return;
        ApplyNewInstallFolder(dir);
    }

    void OnResetInstallPath(object sender, RoutedEventArgs e)
    {
        var ask = MessageBox.Show(
            this,
            Loc.Get("msg_reset_path"),
            Loc.Get("title_reset"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK)
            return;

        var dir = PickInstallFolder();
        if (dir is null)
            return;

        ApplyNewInstallFolder(dir);
        Log("Install path reset: " + dir);
    }

    string? PickInstallFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.Get("title_choose_folder"),
            Multiselect = false,
        };
        var root = ReadInstallPathBox();
        if (Directory.Exists(root))
            dlg.InitialDirectory = root;
        if (dlg.ShowDialog(this) != true)
            return null;
        var dir = dlg.FolderName?.Trim();
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    void ApplyNewInstallFolder(string dir)
    {
        if (!TryAcceptInstallPath(dir))
            return;
        RememberOutgoingInstallFolder(dir);
        if (NeedsSetup(dir))
            _settings.InitialInstallAccepted = false;

        if (_verifyBusy || _installBusy)
        {
            // AdoptInstallFolderAsync would drop the change on the floor;
            // keep the choice so Browse always takes effect.
            SyncInstallPathBoxes(dir);
            _settings.InstallPath = dir;
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
            SetSimpleStatus(Loc.Get("status_folder_saved"));
            return;
        }

        _ = AdoptInstallFolderAsync(dir, persist: true);
    }

    private void OnInstallRootLostFocus(object sender, RoutedEventArgs e)
    {
        var root = TxtInstallRoot.Text.Trim();
        if (string.IsNullOrEmpty(root) ||
            string.Equals(root, _settings.InstallPath, StringComparison.OrdinalIgnoreCase))
            return;
        ApplyNewInstallFolder(root);
    }

    private void OnArgsLostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        PersistSettingsFromUi();
    }

    private void OnArgsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        RefreshArgPreviews();
        PersistSettingsFromUi();
    }

    private void OnLaunchOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        PersistSettingsFromUi();
        RefreshArgPreviews();
    }

    private void PersistSettingsFromUi()
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;

        var chosen = ReadInstallPathBox();
        if (!string.IsNullOrWhiteSpace(chosen) && !TryAcceptInstallPath(chosen, quiet: true))
            chosen = _settings.InstallPath;
        SyncInstallPathBoxes(chosen);
        _settings.InstallPath = chosen;
        _settings.ClientLaunchArguments = TxtClientArgs.Text ?? string.Empty;
        _settings.DediLaunchArguments = TxtDediArgs.Text ?? string.Empty;
        _settings.OfflineNoAuth = IsOfflineOn();
        _settings.DediHostOnline = IsDediHostOnlineOn();
        if (IsDeveloperOn() != _settings.DevProfile)
            ApplyDeveloperMaster(IsDeveloperOn());
        _settings.Cheats = IsCheatsOn();
        _settings.FilterMapsByPlaylist = ChkFilterMaps.IsChecked == true;
        if (ChkShowUnlistedMaps is not null)
            _settings.ShowUnlistedMaps = ChkShowUnlistedMaps.IsChecked == true;
        if (ChkOpenConsoleOnLaunch is not null)
            _settings.OpenConsoleOnLaunch = ChkOpenConsoleOnLaunch.IsChecked == true;
        _settings.DediPlaylist = SelectedPlaylistId();
        _settings.DediMap = SelectedMap();
        _settings.DediPort = SelectedPort();
        _settings.DediPasswordEnabled = IsPasswordProtectOn();
        _settings.DediPassword = StoredPasswordText();
        CaptureModeSettingsFromUi();

        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log($"Settings save failed: {ex.Message}");
        }

        RefreshArgPreviews();
    }

    private void CaptureModeSettingsFromUi()
    {
        if (_selectedMode is not null)
            _settings.LastModePlaylist = _selectedMode.Id;

        foreach (var card in _modeCards)
        {
            var stem = card.SelectedMapStem;
            if (!string.IsNullOrWhiteSpace(stem))
                _settings.ModeMaps[card.Id] = stem;
        }
    }

    private void RefreshArgPreviews()
    {
        if (TxtClientPreview is null || TxtDediPreview is null)
            return;

        try
        {
            var client = BuildClientArgs(includeConnect: false);
            var dedi = BuildDediArgs(mapOverride: null);
            var dediLine = LaunchArgs.RedactSensitiveArgs(LaunchArgs.FormatArgumentsOnly(dedi));
            TxtClientPreview.Text = "full: r5apex.exe " + LaunchArgs.FormatArgumentsOnly(client);
            // sv_cheats only when Dev profile is on (or typed into Extra args).
            if (ContainsArgToken(dedi, "+sv_cheats"))
            {
                TxtDediPreview.Text =
                    "full: r5apex_ds.exe " + dediLine +
                    Environment.NewLine +
                    "note: +sv_cheats 1 (Cheats or Extra args)";
            }
            else
            {
                TxtDediPreview.Text =
                    "full: r5apex_ds.exe " + dediLine +
                    Environment.NewLine +
                    "note: sv_cheats off unless Extra args / console";
            }
        }
        catch (Exception ex)
        {
            TxtClientPreview.Text = "preview error: " + ex.Message;
            TxtDediPreview.Text = "";
        }
    }

    private static bool ContainsArgToken(IReadOnlyList<string> args, string token)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnOpenInstall(object sender, RoutedEventArgs e)
    {
        var root = TxtInstallRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show(this, Loc.Get("msg_dir_missing"), Loc.Get("title_open"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true });
            UpdateStatus(Loc.Format("status_opened", root));
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private void OnDownloadFull(object sender, RoutedEventArgs e)
    {
        if (_settings.SimpleMode && NeedsSetup(ReadInstallPathBox()))
            ApplySimpleTab(SimpleTab.Play);
        _ = RunInstallAsync(InstallMode.Full);
    }

    private void OnVerifyFiles(object sender, RoutedEventArgs e) =>
        _ = RunVerifyFilesAsync();

    /// <summary>
    /// Check every file against the manifest and say what is wrong. Deliberately
    /// does not fix anything: repair is a separate button so a player can look
    /// before anything is rewritten.
    /// </summary>
    private async Task RunVerifyFilesAsync()
    {
        if (_installBusy || _verifyBusy)
            return;

        var root = ReadInstallPathBox();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            SetSimpleStatus(Loc.Get("status_no_game_folder"));
            return;
        }

        _verifyBusy = true;
        _installCts = new CancellationTokenSource();
        try
        {
            PaintInstallControls();
            ShowSimpleInstallBar(true);
            Log("Verify: checking every file against the manifest.");

            var health = await ContentInstallService.VerifyExistingAsync(
                _manifest, root, CreateVerifyProgress(), _installCts.Token)
                .ConfigureAwait(true);

            InvalidateHealthMemo();
            var summary = health.IsReady
                ? Loc.Get("verify_ok")
                : Loc.Format("verify_problems", health.Summary);
            Log("Verify: " + summary);
            SetSimpleStatus(summary);
            if (!_settings.SimpleMode)
                MessageBox.Show(this, summary, Loc.Get("verify_files"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("Verify cancelled.");
        }
        catch (Exception ex)
        {
            Log("Verify failed: " + ex.Message);
            SetSimpleStatus(ex.Message);
        }
        finally
        {
            _verifyBusy = false;
            _installCts?.Dispose();
            _installCts = null;
            ShowSimpleInstallBar(false);
            PaintInstallControls();
            RefreshInstallStateLabels();
        }
    }

    private void OnRepair(object sender, RoutedEventArgs e) =>
        _ = RunRepairAsync();

    private async Task RunRepairAsync(bool resumeIncomplete = false)
    {
        if (_installBusy)
            return;

        PersistSettingsFromUi();
        var installPath = ReadInstallPathBox();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            MessageBox.Show(this, Loc.Get("msg_set_dir"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_manifest is null)
            await LoadChannelAsync().ConfigureAwait(true);
        if (_manifest is null)
        {
            MessageBox.Show(this,
                Loc.Get("msg_no_channel"),
                Loc.Get("title_content"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _contentRepair = true;
        _installBusy = true;
        SetInstallButtonsEnabled(false);
        RefreshSimplePlayButton();
        try
        {
            Log(resumeIncomplete
                ? "Content repair: fetch missing and replace damaged files."
                : "Content health check + auto-repair (corrupt only; updates need Check for updates)…");
            if (TxtInstallStatus is not null)
                TxtInstallStatus.Text = Loc.Get("status_checking_health");
            BarInstall.Value = 0;
            ShowSimpleInstallBar(true);
            SetSimpleStatus(Loc.Get("status_repairing"));

            var progress = CreateInstallProgress();
            var gate = await RefreshDownloadGateAsync().ConfigureAwait(true);
            var health = await ContentInstallService.EnsureReadyAsync(
                _manifest,
                installPath,
                requireClient: true,
                requireServer: true,
                autoRepairCorrupt: true,
                autoApplyUpdates: resumeIncomplete,
                progress: progress,
                decideOverlay: DecideOverlayEdits,
                allowContent: gate.Content,
                allowPlatform: gate.Platform).ConfigureAwait(true);

            RefreshInstallStateLabels();
            if (health.NeedsUpdate)
            {
                if (TxtInstallStatus is not null)
                    TxtInstallStatus.Text = Loc.Get("health_update");
                UpdateStatus(Loc.Get("status_content_update_short"));
                SetSimpleStatus(Loc.Get("status_update_available"));
                if (!_settings.SimpleMode)
                {
                    MessageBox.Show(this,
                        Loc.Format("msg_content_update", health.Summary),
                        Loc.Get("title_content_update"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else if (health.IsReady || !health.Enforced)
            {
                BarInstall.Value = 100;
                if (BarSimpleInstall is not null)
                    BarSimpleInstall.Value = 100;
                if (TxtInstallStatus is not null)
                    TxtInstallStatus.Text = Loc.Format("status_health_ok", health.Summary);
                UpdateStatus(Loc.Get("status_content_healthy"));
                SetSimpleStatus(Loc.Get("ready_to_play"));
            }
            else
            {
                if (TxtInstallStatus is not null)
                    TxtInstallStatus.Text = Loc.Format("status_still_blocked", health.Summary);
                UpdateStatus(Loc.Get("status_content_not_ready_short"));
                SetSimpleStatus(Loc.Get("status_files_need_work"));
            }
        }
        catch (Exception ex)
        {
            Log("Repair error: " + ex.Message);
            TxtInstallStatus.Text = Loc.Format("status_error", ex.Message);
            SetError(ex.Message);
            SetSimpleStatus(Loc.Get("status_check_failed"));
        }
        finally
        {
            _installBusy = false;
            _contentRepair = false;
            SetInstallButtonsEnabled(true);
            ShowSimpleInstallBar(false);
            RefreshInstallStateLabels();
        }
    }

    private async Task RunInstallAsync(InstallMode mode)
    {
        if (_installBusy)
            return;

        PersistSettingsFromUi();
        var installPath = ReadInstallPathBox();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            if (_settings.SimpleMode)
            {
                SetSimpleStatus(Loc.Get("status_pick_folder"));
                TxtSimpleInstallRoot?.Focus();
                return;
            }
            MessageBox.Show(this, Loc.Get("msg_set_dir"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_manifest is null)
            await LoadChannelAsync().ConfigureAwait(true);
        if (_manifest is null)
        {
            if (_settings.SimpleMode)
            {
                SetSimpleStatus(Loc.Get("status_catalog_missing"));
                return;
            }
            MessageBox.Show(this,
                Loc.Get("msg_no_channel"),
                Loc.Get("title_content"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var gate = await RefreshDownloadGateAsync().ConfigureAwait(true);
        if (!gate.AnyLane)
        {
            SetSimpleStatus(Loc.Get("status_downloads_off"));
            MessageBox.Show(this, Loc.Get("msg_downloads_off"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!gate.Content && NeedsSetup(installPath))
        {
            SetSimpleStatus(Loc.Get("status_downloads_off"));
            MessageBox.Show(this, Loc.Get("msg_downloads_off_content"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryAcceptInstallPath(installPath))
            return;

        if (!ConfirmAndGateInstall(mode, installPath))
            return;

        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        _installRun = new InstallRunControl();
        _contentRepair = false;
        _installBusy = true;
        SetInstallButtonsEnabled(false);
        RefreshSimplePlayButton();
        PaintInstallControls();
        try
        {
            Log($"Content install start: mode={mode} path={installPath}");
            TxtInstallStatus.Text = Loc.Format("status_installing_mode", mode);
            BarInstall.Value = 0;
            ShowSimpleInstallBar(true);
            SetSimpleStatus(mode == InstallMode.Full ? Loc.Get("status_installing_game") : Loc.Get("status_installing_server"));
            UpdateStatus(Loc.Format("status_content_colon", mode));

            var progress = CreateInstallProgress();

            var state = await ContentInstallService.InstallAsync(
                _manifest,
                mode,
                installPath,
                fetcher: null,
                progress: progress,
                cancel: _installCts.Token,
                runControl: _installRun,
                decideOverlay: DecideOverlayEdits,
                allowContent: gate.Content,
                allowPlatform: gate.Platform).ConfigureAwait(true);

            RefreshInstallStateLabels(state);
            if (state.Incomplete || !string.IsNullOrWhiteSpace(state.LastError))
            {
                var err = state.LastError ?? "incomplete";
                Log($"Content install incomplete: {err}");
                TxtInstallStatus.Text = Loc.Format("status_failed", err);
                UpdateStatus(Loc.Get("status_content_failed"));
                SetSimpleStatus(Loc.Get("status_install_incomplete"));
            }
            else
            {
                BarInstall.Value = 100;
                if (BarSimpleInstall is not null)
                    BarSimpleInstall.Value = 100;
                TxtInstallStatus.Text =
                    $"OK mode={mode} client={state.ClientReady} server={state.ServerReady}";
                Log($"Content install complete: client_ready={state.ClientReady} server_ready={state.ServerReady}");
                UpdateStatus(Loc.Get("status_content_complete"));
                SetSimpleStatus(Loc.Get("ready_to_play"));
                ReloadPlaylistsAndMaps(selectSaved: true);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Content install cancelled.");
            TxtInstallStatus.Text = Loc.Get("status_cancelled");
            UpdateStatus(Loc.Get("status_content_cancelled"));
            SetSimpleStatus(Loc.Get("status_download_cancelled"));
        }
        catch (Exception ex)
        {
            Log("Content install error: " + ex.Message);
            TxtInstallStatus.Text = Loc.Format("status_error", ex.Message);
            SetError(ex.Message);
            SetSimpleStatus(Loc.Get("status_install_failed"));
            if (!_settings.SimpleMode)
            {
                MessageBox.Show(this, Loc.Format("msg_content_failed", ex.Message), Loc.Get("title_app"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _installBusy = false;
            _installCts?.Dispose();
            _installCts = null;
            _installRun = null;
            SetInstallButtonsEnabled(true);
            ShowSimpleInstallBar(false);
            InvalidateHealthMemo();
            RefreshInstallStateLabels();
            PaintInstallControls();
            ApplyPendingHdAction();
        }
    }

    private Progress<ContentInstallProgress> CreateInstallProgress() =>
        new(p => ApplyInstallProgress(p, logEvery: false));

    private void SetInstallButtonsEnabled(bool enabled)
    {
        if (BtnDownloadFull is not null)
            BtnDownloadFull.IsEnabled = enabled;
        if (BtnRepair is not null)
            BtnRepair.IsEnabled = enabled;
        RefreshSimplePlayButton();
    }

    private void RefreshInstallStateLabels(InstallState? known = null)
    {
        _ = known;
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        var health = HealthForUi(root);

        if (TxtHealth is not null)
        {
            TxtHealth.Text = HealthLine(health);
            TxtHealth.Foreground = health.Overall switch
            {
                InstallHealthStatus.Ready => System.Windows.Media.Brushes.LightGreen,
                InstallHealthStatus.UpdateAvailable => System.Windows.Media.Brushes.Gold,
                InstallHealthStatus.Corrupted => System.Windows.Media.Brushes.OrangeRed,
                InstallHealthStatus.Incomplete => System.Windows.Media.Brushes.Orange,
                _ => System.Windows.Media.Brushes.Gray,
            };
        }

        RefreshHdTextures();
        MaybeAnnounceHdTextures();

        RefreshSimplePlayButton(health);
        RefreshDx12Chrome();
    }

    static string HealthLine(InstallHealthReport health)
    {
        if (!health.Enforced)
            return Loc.Get("health_local");
        return health.Overall switch
        {
            InstallHealthStatus.Ready => Loc.Get("health_ready"),
            InstallHealthStatus.UpdateAvailable => Loc.Get("health_update"),
            InstallHealthStatus.Corrupted => Loc.Get("health_corrupt"),
            InstallHealthStatus.Incomplete => Loc.Get("health_incomplete"),
            InstallHealthStatus.Missing => Loc.Get("health_missing"),
            _ => health.Summary,
        };
    }

    /// <summary>
    /// Content health gate: refuse when enforced pack install is missing/incomplete/outdated.
    /// Corrupted: attempt auto-repair first (async callers use TryContentHealthGateAsync).
    /// Local master without pack assets stays preflight-only.
    /// </summary>
    private bool TryInstallStatePlayGate(LaunchRole role, out string? refuseReason)
    {
        refuseReason = null;
        var root = TxtInstallRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
            return true;

        if (_manifest is null)
            TryLoadChannelFromCache();

        var requireClient = role == LaunchRole.Client;
        var requireServer = role == LaunchRole.Dedicated;
        // Never assess on the UI thread: with per-file hashing behind it that
        // freezes the window. A stale answer plus a background refresh beats a
        // stall, and the async gate still runs before play.
        if (_healthMemo is null)
        {
            KickHealthRefresh(root);
            return true;
        }
        var health = _healthMemo;
        if (!HealthMemoFresh(root))
            KickHealthRefresh(root);

        if (!health.Enforced)
            return true;

        if (health.NeedsUpdate)
        {
            if (LaneBlocksPlay(health, CachedDownloadGate(), requireClient, requireServer))
            {
                refuseReason =
                    Loc.Format("gate_update_before_play", health.Summary);
                return false;
            }
            Log("Update available but downloads are off; allowing play.");
        }

        if (health.NeedsRepair)
        {
            refuseReason = Loc.Format("gate_corrupt", health.Summary);
            return false;
        }

        if (health.BlocksPlay && !health.NeedsUpdate)
        {
            refuseReason = Loc.Format("gate_not_ready", health.Summary);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Quick Play / full dual-role gate: client+server required; auto-repair corrupt; block updates.
    /// </summary>
    private async Task<(bool ok, string? reason)> EnsurePlayContentAsync(
        bool requireClient,
        bool requireServer,
        bool autoRepairCorrupt)
    {
        var root = TxtInstallRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
            return (true, null);

        if (!TryAcceptInstallPath(root, quiet: true))
        {
            return (false,
                "Pick a folder that is not AppData or the launcher's own folder.");
        }

        if (_manifest is null)
            await LoadChannelAsync().ConfigureAwait(true);

        var health = await Task.Run(() =>
        {
            var report = ContentInstallService.Assess(
                _manifest, root, requireClient, requireServer);
            _healthMemo = report;
            _healthMemoRoot = root;
            _healthMemoUtc = DateTime.UtcNow;
            return report;
        }).ConfigureAwait(true);
        if (!health.Enforced)
            return (true, null);

        if (health.NeedsUpdate)
        {
            var gate = await RefreshDownloadGateAsync().ConfigureAwait(true);
            if (LaneBlocksPlay(health, gate, requireClient, requireServer))
            {
                return (false,
                    Loc.Format("gate_update_first", health.Summary));
            }
            Log("Update available but downloads are off; allowing play.");
        }

        if (health.NeedsRepair && autoRepairCorrupt)
        {
            var channel = _manifest;
            if (channel is null)
                return (false, Loc.Format("gate_corrupt", health.Summary));

            Log("Auto-repair before play: " + health.Summary);
            TxtInstallStatus.Text = Loc.Get("status_auto_repairing");
            if (_settings.SimpleMode)
                SetSimpleStatus(Loc.Get("status_repairing"));
            try
            {
                var gate = CachedDownloadGate();
                health = await ContentInstallService.EnsureReadyAsync(
                    channel,
                    root,
                    requireClient,
                    requireServer,
                    autoRepairCorrupt: true,
                    autoApplyUpdates: false,
                    progress: CreateInstallProgress(),
                    decideOverlay: DecideOverlayEdits,
                    allowContent: gate.Content,
                    allowPlatform: gate.Platform).ConfigureAwait(true);
                _healthMemo = health;
                _healthMemoRoot = root;
                _healthMemoUtc = DateTime.UtcNow;
                RefreshInstallStateLabels();
            }
            catch (Exception ex)
            {
                return (false, Loc.Format("gate_auto_repair_failed", ex.Message));
            }
        }

        if (health.NeedsUpdate)
        {
            if (LaneBlocksPlay(health, CachedDownloadGate(), requireClient, requireServer))
            {
                return (false,
                    Loc.Format("gate_channel_newer", health.Summary));
            }
        }
        else if (health.BlocksPlay)
            return (false, Loc.Format("gate_not_ready", health.Summary));

        return (true, null);
    }

    private async void OnLaunchClient(object sender, RoutedEventArgs e)
    {
        PersistSettingsFromUi();
        var (ok, reason) = await EnsurePlayContentAsync(
            requireClient: true, requireServer: false, autoRepairCorrupt: true)
            .ConfigureAwait(true);
        if (!ok)
        {
            Log("Play Client refused: " + reason);
            SetError(reason ?? Loc.Get("msg_content_not_ready"));
            MessageBox.Show(this, reason ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (RendererRestartPending())
        {
            await RestartClientForRendererAsync().ConfigureAwait(true);
            return;
        }

        SpawnRole(LaunchRole.Client, BuildClientArgs(includeConnect: false));
    }

    private async void OnLaunchDedi(object sender, RoutedEventArgs e)
    {
        PersistSettingsFromUi();
        var (ok, reason) = await EnsurePlayContentAsync(
            requireClient: false, requireServer: true, autoRepairCorrupt: true)
            .ConfigureAwait(true);
        if (!ok)
        {
            Log("Play Dedi refused: " + reason);
            SetError(reason ?? Loc.Get("msg_content_not_ready"));
            MessageBox.Show(this, reason ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryHostPassword(out _, out var pwRefuse))
        {
            Log("Play Dedi refused: " + pwRefuse);
            SetError(pwRefuse ?? Loc.Get("msg_content_not_ready"));
            MessageBox.Show(this, pwRefuse ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var launchPl = SelectedPlaylistId().Trim().ToLowerInvariant();
        var launchMap = SelectedMap().Trim().ToLowerInvariant();
        if ((!string.IsNullOrWhiteSpace(launchPl) && !LocalRcon.IsSafeIdentifier(launchPl)) ||
            (!string.IsNullOrWhiteSpace(launchMap) && !LocalRcon.IsSafeIdentifier(launchMap)))
        {
            const string badId = "Play Dedi refused: playlist or map id contains illegal characters.";
            Log(badId);
            SetError(badId);
            return;
        }
        RefreshDediModPolicyUi();
        PersistDediModPolicy();
        SpawnRole(LaunchRole.Dedicated, BuildDediArgs(mapOverride: null));
        _livePlaylist = SelectedPlaylistId();
        _liveMap = SelectedMap();
    }

    private void OnKillClient(object sender, RoutedEventArgs e)
    {
        DisarmHostedMatch();
        DropHostedConsoles();
        var root = TxtInstallRoot.Text.Trim();
        var n = ProcessSpawner.KillRole(LaunchRole.Client, root);
        _lastClientPid = null;
        RestoreSessionMods(force: true);
        Log($"Kill Client: stopped {n} process(es)");
        UpdateKillButtons();
        UpdateStatus($"Killed client ({n})  |  {PidLine()}");
    }

    private void OnKillDedi(object sender, RoutedEventArgs e)
    {
        _playCts?.Cancel();
        DisarmHostedMatch();
        DropHostedConsoles();
        var root = TxtInstallRoot.Text.Trim();
        var n = ProcessSpawner.KillRole(LaunchRole.Dedicated, root);
        _lastDediPid = null;
        DropLocalRcon();
        Log($"Kill Dedi: stopped {n} process(es)");
        UpdateKillButtons();
        UpdateStatus($"Killed dedi ({n})  |  {PidLine()}");
    }

    private async void OnQuickPlay(object sender, RoutedEventArgs e)
    {
        if (SessionLive)
        {
            await StopSessionAsync().ConfigureAwait(true);
            return;
        }

        var playlist = SelectedPlaylistId();
        if (string.IsNullOrWhiteSpace(playlist))
            playlist = "survival_dev";
        // Advanced PLAY always uses Divided Moon; sync map combo after gate succeeds.
        await RunPlayAsync(playlist, QuickPlayMap, syncMapCombo: true).ConfigureAwait(true);
    }

    /// <summary>Advanced header PLAY mirrors the Simple button's play/stop state.</summary>
    private void PaintQuickPlayButton()
    {
        if (BtnQuickPlay is null)
            return;

        var stop = SessionLive;
        BtnQuickPlay.Content = stop ? Loc.Get("stop") : Loc.Get("play");
        if (BtnQuickPlay.TryFindResource(stop ? "DangerButton" : "PrimaryButton") is Style style)
            BtnQuickPlay.Style = style;
    }

    /// <summary>
    /// Shared play path: content gate, kill, dedi, delay, client connect.
    /// Used by Advanced QUICK PLAY and Simple PLAY.
    /// </summary>
    private async Task RunPlayAsync(string playlistId, string map, bool syncMapCombo = false)
    {
        if (_quickPlayBusy)
            return;

        if (ModeCardViewModel.IsLobbyPlaylist(playlistId) || IsLobbyStem(map))
        {
            playlistId = ModeCardViewModel.LobbyLaunchPlaylist;
            map = ModeCardViewModel.LobbyMapStem;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(playlistId))
                playlistId = "survival_dev";
            if (string.IsNullOrWhiteSpace(map))
                map = QuickPlayMap;
        }

        var simple = _settings.SimpleMode;
        _quickPlayBusy = true;
        if (BtnQuickPlay is not null)
            BtnQuickPlay.IsEnabled = false;
        RefreshSimplePlayButton();
        try
        {
            PersistSettingsFromUi();
            var root = TxtInstallRoot.Text.Trim();
            var port = SelectedPort();

            // Both tracks required; auto-repair corrupt; refuse CHANNEL updates.
            var (contentOk, contentReason) = await EnsurePlayContentAsync(
                requireClient: true,
                requireServer: true,
                autoRepairCorrupt: true).ConfigureAwait(true);
            if (!contentOk)
            {
                Log("Play refused: " + contentReason);
                if (simple)
                {
                    SetSimpleStatus(PlainContentGateMessage(contentReason));
                    RefreshSimplePlayButton();
                }
                else
                {
                    SetError(contentReason ?? Loc.Get("msg_content_not_ready"));
                    MessageBox.Show(this, contentReason ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            if (syncMapCombo)
            {
                _suppressDediUi = true;
                try { SelectMap(map); }
                finally { _suppressDediUi = false; }
                PersistSettingsFromUi();
            }

            Log($"Play: dedi +map {map} playlist={playlistId} port={port}; client waits for host-ready then +connect 127.0.0.1:{port}");
            UpdateStatus(Loc.Get("status_starting_server"));
            if (simple)
                SetSimpleStatus(Loc.Get("status_starting_server"));

            DisarmHostedMatch();
            _playCts?.Cancel();
            _playCts?.Dispose();
            _playCts = new CancellationTokenSource();
            var playCt = _playCts.Token;
            // Kill prior on background so UI stays responsive.
            var killed = await Task.Run(() => ProcessSpawner.KillAll(root)).ConfigureAwait(true);
            if (killed > 0)
                Log($"Play: killed {killed} prior process(es)");
            // Play Local owns the port: a dedi left over from a crash or another
            // deploy root survives the path-scoped kill above and the new one then
            // spins on WSAEADDRINUSE forever.
            var strays = await Task.Run(ProcessSpawner.KillAllDediImages).ConfigureAwait(true);
            if (strays > 0)
                Log($"Play: killed {strays} stray dedi instance(s)");
            ResetClientConsoleForNewSession();

            var portFree = await Task.Run(() => ProcessSpawner.WaitPortFree(port, 5000), playCt)
                .ConfigureAwait(true);
            if (!portFree)
            {
                var busy = Loc.Format("play_port_busy", port);
                Log("Play refused: " + busy);
                if (simple)
                    SetSimpleStatus(busy);
                else
                    SetError(busy);
                return;
            }

            if (!TryHostPassword(out _, out var pwRefuse))
            {
                Log("Play refused: " + pwRefuse);
                if (simple)
                    SetSimpleStatus(pwRefuse ?? Loc.Get("msg_content_not_ready"));
                else
                {
                    SetError(pwRefuse ?? Loc.Get("msg_content_not_ready"));
                    MessageBox.Show(this, pwRefuse ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            var plCheck = playlistId.Trim().ToLowerInvariant();
            var mapCheck = map.Trim().ToLowerInvariant();
            if ((!string.IsNullOrWhiteSpace(playlistId) && !LocalRcon.IsSafeIdentifier(plCheck)) ||
                (!string.IsNullOrWhiteSpace(map) && !LocalRcon.IsSafeIdentifier(mapCheck)))
            {
                const string badId = "Play refused: playlist or map id contains illegal characters.";
                Log(badId);
                if (simple)
                    SetSimpleStatus(badId);
                else
                    SetError(badId);
                return;
            }

            _hostReady?.Dispose();
            _hostReady = HostReadyGate.Create();
            RefreshDediModPolicyUi();
            PersistDediModPolicy();
            var dediArgs = BuildDediArgs(mapOverride: map, playlistOverride: playlistId);
            var dediResult = SpawnRoleResult(
                LaunchRole.Dedicated,
                dediArgs,
                quiet: simple,
                forceHostedConsole: true,
                extraEnv: _hostReady.ToEnvironment());
            if (!dediResult.Ok || dediResult.Process is null)
            {
                if (simple)
                    SetSimpleStatus(Loc.Get("status_server_failed"));
                else
                    SetError(dediResult.Error ?? "dedi spawn failed");
                return;
            }

            _lastDediPid = dediResult.ProcessId;
            _livePlaylist = playlistId;
            _liveMap = map;
            ArmHostedMatch();
            Log($"[Play] dedi pid={dediResult.ProcessId}");
            UpdateKillButtons();
            if (BtnQuickPlay is not null)
                BtnQuickPlay.IsEnabled = true;
            UpdateStatus(Loc.Get("status_waiting_server"));
            if (simple)
            {
                SetSimpleStatus(Loc.Get("status_waiting_server"));
                // The wait can run a minute; the console is the only place
                // that shows the server actually making progress.
                ApplySimpleTab(SimpleTab.Console);
            }

            HostReadyWait wait;
            bool readyUnsignaled;
            var startedUtc = DateTime.UtcNow;
            try
            {
                (wait, readyUnsignaled) = await WaitHostReadyAsync(
                    dediResult.Process, playCt, simple).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (readyUnsignaled)
                Log($"[Play] host ready not signaled after {HostReadySpawnAnywaySeconds}s; starting the client anyway");

            if (playCt.IsCancellationRequested || !_hostedLocalMatch)
                return;

            if (wait == HostReadyWait.ProcessDied || wait == HostReadyWait.Fatal)
            {
                QueueHostedServerCrash();
                return;
            }

            if (wait != HostReadyWait.Ready)
                return;

            try
            {
                if (dediResult.Process.HasExited)
                {
                    QueueHostedServerCrash();
                    return;
                }
            }
            catch
            {
                QueueHostedServerCrash();
                return;
            }

            Log($"[Play] host ready after {(int)(DateTime.UtcNow - startedUtc).TotalSeconds}s");
            _hostHeartbeatGraceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            _hostHeartbeatMisses = 0;
            UpdateStatus(Loc.Get("status_starting_game"));
            if (simple)
                SetSimpleStatus(Loc.Get("status_starting_game"));

            // 127.0.0.1 last via IncludeConnect (after extras).
            var clientArgs = BuildClientArgs(
                includeConnect: true,
                connectHost: "127.0.0.1",
                connectPort: port,
                includePassword: true,
                noTimeout: true);
            var clientResult = SpawnRoleResult(
                LaunchRole.Client, clientArgs, quiet: simple, grantHostConsole: true);
            if (!clientResult.Ok)
            {
                if (simple)
                    SetSimpleStatus(Loc.Get("status_client_failed_dedi_ok"));
                else
                    SetError("client spawn failed — dedi left running: " + (clientResult.Error ?? "unknown"));
                return;
            }

            _lastClientPid = clientResult.ProcessId;
            Log($"[Play] client pid={clientResult.ProcessId} {LaunchArgs.RedactSensitiveArgs(clientResult.CommandLine ?? string.Empty)}");
            UpdateKillButtons();
            UpdateStatus($"Quick Play OK  |  {PidLine()}  |  {map} → 127.0.0.1:{port}");
            if (simple)
                SetSimpleStatus(Loc.Get("status_playing"));
            MaybeOpenConsoleForLaunch();
        }
        catch (OperationCanceledException)
        {
            Log("Play cancelled");
        }
        catch (Exception ex)
        {
            Log("Play failed: " + ex.Message);
            if (simple)
            {
                SetSimpleStatus(Loc.Get("status_generic_error"));
            }
            else
            {
                SetError(ex.Message);
                MessageBox.Show(this, Loc.Format("msg_play_failed", ex.Message), Loc.Get("title_app"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _hostReady?.Dispose();
            _hostReady = null;
            if (Volatile.Read(ref _crashPromptPosted) == 0)
                EndPlayBusy();
            else
                UpdateKillButtons();
        }
    }

    OverlayExtractPolicy DecideOverlayEdits(OverlayEditReport report)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => DecideOverlayEdits(report));
        var choice = OverlayScriptsWindow.Ask(this, report);
        Log(choice == OverlayExtractPolicy.KeepEdits
            ? $"Leaving {report.Total} edited script file(s)."
            : $"Restoring official scripts ({report.Total} edited).");
        return choice;
    }

    private static string PlainContentGateMessage(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Loc.Get("plain_not_ready");
        if (reason.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("newer", StringComparison.OrdinalIgnoreCase))
            return Loc.Get("plain_update");
        if (reason.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("repair", StringComparison.OrdinalIgnoreCase))
            return Loc.Get("plain_repair");
        return Loc.Get("plain_install");
    }

    private static LaunchProfile ProfileFor(bool developer) =>
        developer ? LaunchProfile.ShippingDev : LaunchProfile.ShippingPlayer;

    private LaunchProfile ClientProfile() => ProfileFor(_settings.ClientDevProfile);

    private LaunchProfile DediProfile() => ProfileFor(_settings.DediDevProfile);

    /// <summary>Simple mode's one switch drives both engines.</summary>
    private void ApplyDeveloperMaster(bool on)
    {
        _settings.DevProfile = on;
        _settings.ClientDevProfile = on;
        _settings.DediDevProfile = on;
        SyncDeveloperChecks();
    }

    private void SyncDeveloperChecks()
    {
        var restore = _suppressArgsPersist;
        _suppressArgsPersist = true;
        try
        {
            if (ChkSimpleDeveloper is not null)
                ChkSimpleDeveloper.IsChecked = _settings.DevProfile;
            if (ChkClientDeveloper is not null)
                ChkClientDeveloper.IsChecked = _settings.ClientDevProfile;
            if (ChkDediDeveloper is not null)
                ChkDediDeveloper.IsChecked = _settings.DediDevProfile;
        }
        finally
        {
            _suppressArgsPersist = restore;
        }
    }

    private void OnClientDeveloperChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        _settings.ClientDevProfile = ChkClientDeveloper?.IsChecked == true;
        SaveDeveloperSplit("client");
    }

    private void OnDediDeveloperChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        _settings.DediDevProfile = ChkDediDeveloper?.IsChecked == true;
        SaveDeveloperSplit("dedi");
    }

    private void SaveDeveloperSplit(string side)
    {
        _settings.DevProfile = _settings.ClientDevProfile && _settings.DediDevProfile;
        SyncDeveloperChecks();

        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }

        var on = side == "client" ? _settings.ClientDevProfile : _settings.DediDevProfile;
        Log($"Developer ({side}): {(on ? "-dev -devsdk" : "off")}");
        RefreshArgPreviews();
    }

    private bool IsCheatsOn() =>
        ChkSimpleCheats?.IsChecked == true;

    private bool IsDeveloperOn() =>
        ChkSimpleDeveloper?.IsChecked == true;

    private bool IsOfflineOn() =>
        ChkSimpleOffline?.IsChecked == true;

    private bool IsDediHostOnlineOn() =>
        ChkDediOnline?.IsChecked == true;

    private SpireVisibility DediVisibility() =>
        IsDediHostOnlineOn() && !IsOfflineOn()
            ? SpireVisibility.Public
            : SpireVisibility.Offline;

    private void SetCheckSilently(CheckBox? box, bool value)
    {
        if (box is null)
            return;

        var prev = _suppressArgsPersist;
        _suppressArgsPersist = true;
        try { box.IsChecked = value; }
        finally { _suppressArgsPersist = prev; }
    }

    private void ApplyOfflineNoAuth(bool on)
    {
        SetCheckSilently(ChkSimpleOffline, on);
        _settings.OfflineNoAuth = on;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        RefreshArgPreviews();
    }

    private void OnDediHostOnlineChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressArgsPersist)
            return;

        if (IsDediHostOnlineOn() && IsOfflineOn())
        {
            ApplyOfflineNoAuth(false);
            Log("Offline cleared: a server listed on the master server has to authenticate joiners");
        }

        _settings.OfflineNoAuth = IsOfflineOn();
        _settings.DediHostOnline = IsDediHostOnlineOn();
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }

        Log(_settings.DediHostOnline
            ? "Dedicated server: online (+spire_host_visibility 2, listed on the master server)"
            : "Dedicated server: offline (+spire_host_visibility 0)");
        RefreshArgPreviews();
    }

    /// <summary>Offline state of the most recently spawned client.</summary>
    private bool _lastClientOfflineAuth;

    /// <summary>The play bar and the server browser each own a copy of the picker.</summary>
    private IEnumerable<Button> ResolutionButtons()
    {
        if (BtnResolutionPreset is not null)
            yield return BtnResolutionPreset;
        if (BtnServersResolution is not null)
            yield return BtnServersResolution;
    }

    private void BuildResolutionPresetMenu(Button? target = null)
    {
        target ??= BtnResolutionPreset;
        if (target is null)
            return;

        var itemStyle = (Style)FindResource("DarkMenuItem");
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = target,
            Placement = PlacementMode.Top,
        };

        foreach (var mode in ResolutionCatalog.WindowModes)
        {
            var item = new MenuItem
            {
                Header = Loc.Get(ResolutionCatalog.LocKey(mode)),
                Style = itemStyle,
                Tag = mode,
                IsCheckable = true,
                IsChecked = mode == _settings.ClientWindowMode,
            };
            item.Click += OnWindowModePicked;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });

        foreach (var group in ResolutionCatalog.Groups())
        {
            // ItemContainerStyle has to be restated per group; a MenuItem does not
            // inherit the one the ContextMenu applies to its own children.
            var header = new MenuItem
            {
                Header = group.Caption,
                Style = itemStyle,
                ItemContainerStyle = itemStyle,
            };

            foreach (var preset in group.Presets)
            {
                var item = new MenuItem { Header = preset.Label, Tag = preset };
                item.Click += OnResolutionPresetPicked;
                header.Items.Add(item);
            }

            menu.Items.Add(header);
        }

        target.ContextMenu = menu;
    }
    private void OnResolutionPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button target)
            return;
        BuildResolutionPresetMenu(target);
        if (target.ContextMenu is not ContextMenu menu)
            return;
        menu.IsOpen = true;
    }

    private void OnWindowModePicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClientWindowMode mode })
            return;
        CommitClientWindowMode(mode);
    }

    private void CommitClientWindowMode(ClientWindowMode mode)
    {
        _settings.ClientWindowMode = mode;
        SyncWindowModeChecks();

        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }

        Log($"Client window mode: {mode}");
        LogFullscreenPresentation();
        RefreshArgPreviews();
    }

    /// <summary>The three mode items are a radio group; IsCheckable alone would
    /// let a click leave two of them ticked.</summary>
    private void SyncWindowModeChecks()
    {
        foreach (var button in ResolutionButtons())
        {
            if (button.ContextMenu is not ContextMenu menu)
                continue;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag is ClientWindowMode mode)
                    item.IsChecked = mode == _settings.ClientWindowMode;
            }
        }
    }

    private void OnResolutionPresetPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ResolutionCatalog.Preset preset })
            return;
        CommitClientResolution(preset.Width, preset.Height);
    }

    private void OnClientResolutionLostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;

        var servers = ReferenceEquals(sender, TxtServersWidth)
            || ReferenceEquals(sender, TxtServersHeight);
        var width = servers ? TxtServersWidth : TxtClientWidth;
        var height = servers ? TxtServersHeight : TxtClientHeight;

        CommitClientResolution(
            ParseResolutionField(width?.Text, _settings.ClientWidth),
            ParseResolutionField(height?.Text, _settings.ClientHeight));
    }

    /// <summary>A resolution is always sent, so this never stores or shows a blank.</summary>
    private void CommitClientResolution(int width, int height)
    {
        _settings.ClientWidth = ResolutionCatalog.ClampDimension(width, ResolutionCatalog.DefaultWidth);
        _settings.ClientHeight = ResolutionCatalog.ClampDimension(height, ResolutionCatalog.DefaultHeight);

        ApplyResolutionFields();

        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }

        Log($"Client resolution: {_settings.ClientWidth}x{_settings.ClientHeight}");
        LogFullscreenPresentation();
        RefreshArgPreviews();
    }

    private void LogFullscreenPresentation()
    {
        if (_settings.ClientWindowMode != ClientWindowMode.Fullscreen)
            return;
        var w = _settings.ClientWidth;
        var h = _settings.ClientHeight;
        if (LaunchArgs.IsListedDisplayMode(w, h))
            Log($"Fullscreen {w}x{h}: exclusive (GPU lists this mode)");
        else
            Log($"Fullscreen {w}x{h}: GPU has no exclusive mode, launching borderless {w}x{h}");
    }

    private void ApplyResolutionFields()
    {
        var restore = _suppressArgsPersist;
        _suppressArgsPersist = true;
        var width = _settings.ClientWidth.ToString(CultureInfo.InvariantCulture);
        var height = _settings.ClientHeight.ToString(CultureInfo.InvariantCulture);
        if (TxtClientWidth is not null)
            TxtClientWidth.Text = width;
        if (TxtClientHeight is not null)
            TxtClientHeight.Text = height;
        if (TxtServersWidth is not null)
            TxtServersWidth.Text = width;
        if (TxtServersHeight is not null)
            TxtServersHeight.Text = height;
        _suppressArgsPersist = restore;
    }

    private static int ParseResolutionField(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return ResolutionCatalog.ClampDimension(value, fallback);
    }
    private IReadOnlyList<string> BuildClientArgs(
        bool includeConnect,
        string connectHost = "localhost",
        int? connectPort = null,
        bool includePassword = false,
        string? connectPassword = null,
        string? map = null,
        bool noTimeout = false,
        bool forceOnline = false)
    {
        var password = connectPassword ?? (includePassword ? ReadPasswordBox() : string.Empty);
        if (!LaunchArgs.IsSafeServerPassword(password))
            password = string.Empty;

        return LaunchArgs.BuildClientArgs(ClientProfile(), new ClientArgOptions
        {
            OfflineNoAuth = !forceOnline && IsOfflineOn(),
            ForceOnline = forceOnline,
            Extra = TxtClientArgs.Text ?? string.Empty,
            IncludeConnect = includeConnect,
            ConnectHost = connectHost,
            ConnectPort = connectPort ?? SelectedPort(),
            Password = password,
            Map = map,
            NoTimeout = noTimeout,
            Language = Loc.Code,
            Width = _settings.ClientWidth,
            Height = _settings.ClientHeight,
            WindowMode = _settings.ClientWindowMode,
        });
    }

    private IReadOnlyList<string> BuildDediArgs(string? mapOverride, string? playlistOverride = null)
    {
        var password = ReadPasswordBox();
        if (!LaunchArgs.IsSafeServerPassword(password))
        {
            Log("server password ignored: illegal characters (no quotes, semicolons, or backslashes)");
            password = string.Empty;
        }

        return LaunchArgs.BuildDediArgs(DediProfile(), new DediArgOptions
        {
            Port = SelectedPort(),
            LaunchPlaylist = playlistOverride ?? SelectedPlaylistId(),
            Map = mapOverride ?? SelectedMap(),
            OfflineNoAuth = IsOfflineOn(),
            Visibility = DediVisibility(),
            Cheats = IsCheatsOn(),
            Password = password,
            Extra = TxtDediArgs.Text ?? string.Empty,
            ExtraTokens = BuildDediModPolicyTokens(),
        });
    }

    private bool IsPasswordProtectOn() =>
        (ChkDediPassword ?? ChkSimplePassword)?.IsChecked == true;

    private string StoredPasswordText()
    {
        if (TxtDediPassword is not null && TxtDediPassword.Password.Length > 0)
            return TxtDediPassword.Password;
        if (TxtSimplePassword is not null && TxtSimplePassword.Password.Length > 0)
            return TxtSimplePassword.Password;
        return _settings.DediPassword ?? string.Empty;
    }

    private string ReadPasswordBox() =>
        IsPasswordProtectOn() ? StoredPasswordText() : string.Empty;

    private void SetPasswordBoxes(string? password)
    {
        var pw = password ?? string.Empty;
        if (TxtDediPassword is not null)
            TxtDediPassword.Password = pw;
        if (TxtSimplePassword is not null)
            TxtSimplePassword.Password = pw;
    }

    private void OnDediPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;

        if (sender is PasswordBox box)
        {
            _suppressArgsPersist = true;
            try
            {
                var pw = box.Password;
                if (!ReferenceEquals(box, TxtDediPassword) && TxtDediPassword is not null)
                    TxtDediPassword.Password = pw;
                if (!ReferenceEquals(box, TxtSimplePassword) && TxtSimplePassword is not null)
                    TxtSimplePassword.Password = pw;
            }
            finally
            {
                _suppressArgsPersist = false;
            }
        }

        PersistSettingsFromUi();
    }

    private void SetPasswordProtectChecked(bool on)
    {
        if (ChkDediPassword is not null)
            ChkDediPassword.IsChecked = on;
        if (ChkSimplePassword is not null)
            ChkSimplePassword.IsChecked = on;
    }

    private void ApplyPasswordProtectVisibility()
    {
        var on = IsPasswordProtectOn();
        if (TxtDediPassword is not null)
            TxtDediPassword.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (TxtSimplePassword is not null)
            TxtSimplePassword.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPasswordProtectChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;

        var on = sender is CheckBox box && box.IsChecked == true;
        _suppressArgsPersist = true;
        try
        {
            SetPasswordProtectChecked(on);
            ApplyPasswordProtectVisibility();
        }
        finally
        {
            _suppressArgsPersist = false;
        }

        PersistSettingsFromUi();
        TryApplyLiveDediPassword();
        if (!on)
            return;

        // The field un-collapses in this pass; focus only lands once it is arranged.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (TxtSimplePassword is { Visibility: Visibility.Visible })
                TxtSimplePassword.Focus();
            else if (TxtDediPassword is { Visibility: Visibility.Visible })
                TxtDediPassword.Focus();
        }));
    }

    private void OnDediPasswordLostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        TryApplyLiveDediPassword();
    }

    private void TryApplyLiveDediPassword()
    {
        if (_consoleServer is null)
            return;

        var pw = ReadPasswordBox();
        if (!LaunchArgs.IsSafeServerPassword(pw))
        {
            Log("live sv_password not applied: illegal characters");
            return;
        }

        var cmd = "sv_password \"" + pw + "\"";
        if (!_consoleServer.TryWriteCommand(cmd))
            return;
        AppendConsoleLine(LaunchRole.Dedicated, "] sv_password ****");
    }

    private void SpawnRole(LaunchRole role, IReadOnlyList<string> args)
    {
        var result = SpawnRoleResult(role, args, ownConsole: true);
        if (result.Ok)
        {
            if (role == LaunchRole.Client)
                _lastClientPid = result.ProcessId;
            else
                _lastDediPid = result.ProcessId;
            UpdateKillButtons();
            UpdateStatus($"Spawned {role}  |  {PidLine()}");
        }
        else
        {
            SetError(result.Error ?? "spawn failed");
        }
    }

    private LaunchResult SpawnRoleResult(
        LaunchRole role,
        IReadOnlyList<string> args,
        bool quiet = false,
        bool forceHostedConsole = false,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        bool grantHostConsole = false,
        bool ownConsole = false)
    {
        var root = TxtInstallRoot.Text.Trim();
        PersistSettingsFromUi();

        // Sync health gate (no auto-repair here — callers EnsurePlayContentAsync first when async).
        if (!TryInstallStatePlayGate(role, out var gateReason))
        {
            Log($"{role} content gate: {gateReason}");
            return new LaunchResult
            {
                Ok = false,
                Error = gateReason ?? "content gate",
            };
        }

        var wantDx12 = role == LaunchRole.Client && _settings.UseDx12;
        if (wantDx12 && !ProcessSpawner.Dx12Available(root))
        {
            var dxMsg = Loc.Get("dx12_missing_exe");
            Log($"{role} preflight FAIL: {dxMsg}");
            if (!quiet)
            {
                MessageBox.Show(this, dxMsg, Loc.Get("directx12"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return new LaunchResult
            {
                Ok = false,
                Error = dxMsg,
            };
        }

        var pre = ProcessSpawner.PreflightRole(root, role, wantDx12);
        if (!pre.Ok)
        {
            var missing = string.Join(Environment.NewLine, pre.MissingPaths);
            Log($"{role} preflight FAIL: missing {missing}");
            if (!quiet)
            {
                MessageBox.Show(this, Loc.Format("preflight_missing", missing), Loc.Get("title_preflight"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return new LaunchResult
            {
                Ok = false,
                Error = $"Missing: {pre.FirstMissingPath}",
            };
        }

        LocalRconSession? session = null;
        if (role == LaunchRole.Dedicated)
        {
            DropLocalRcon();
            session = LocalRconSession.Create(SelectedPort());
        }

        IReadOnlyDictionary<string, string>? extra = session?.ToEnvironment();
        if (role == LaunchRole.Client && grantHostConsole && _localRcon is not null)
            extra = MergeEnv(extra, _localRcon.ToEnvironment());
        extra = MergeEnv(extra, extraEnv);
        // Hosted tap hides AllocConsole. Local play always taps the dedi for
        // host-ready / fatal. Simple (and the console-on-launch box) also tap
        // the client so its pane has every line from process start. Each role
        // keeps its own pane -- the dedi stream is never replaced.
        // Advanced Launch Client / Launch Dedi always keep their own windows.
        var hostClient = _settings.SimpleMode || _settings.OpenConsoleOnLaunch;
        var tap = !ownConsole && (role == LaunchRole.Dedicated
            ? forceHostedConsole || hostClient
            : hostClient);
        if (tap)
            extra = MergeEnv(extra, BeginHostedConsole(role));
        else
            extra = MergeEnv(extra, HostedConsoleTap.ClearedEnvironment());

        var result = ProcessSpawner.Spawn(new LaunchRequest
        {
            InstallRoot = root,
            Role = role,
            Args = args,
            ExtraEnv = extra,
            UseDx12 = wantDx12,
        });

        if (result.Ok)
        {
            if (session is not null)
                _localRcon = session;
            if (role == LaunchRole.Client)
            {
                _launcherOwnsClient = true;
                _lastClientOfflineAuth = ContainsArgToken(args, "-offline");
            }
            Log($"[{role}] pid={result.ProcessId} {LaunchArgs.RedactSensitiveArgs(result.CommandLine ?? string.Empty)}");
        }
        else
        {
            session?.Dispose();
            if (role == LaunchRole.Dedicated)
            {
                _consoleServer?.Dispose();
                _consoleServer = null;
            }
            else
            {
                _consoleClient?.Dispose();
                _consoleClient = null;
                ApplyConsoleView();
            }
            Log($"[{role}] FAIL: {result.Error}");
        }

        return result;
    }

    private void ArmHostedMatch()
    {
        _hostedLocalMatch = true;
        _hostedDediSeen = true;
        _playLocalOwnsClient = true;
        _handlingServerCrash = false;
        Interlocked.Exchange(ref _crashPromptPosted, 0);
        _pendingCrashExcerpt = null;
        _pendingFault = HostedServerFault.Crashed;
        _pendingClientMessage = null;
        _lastScriptError = null;
        _hostHeartbeatGraceUntilUtc = DateTime.MaxValue;
        _hostHeartbeatMisses = 0;
    }

    private int _hostHeartbeatBusy;

    private void TickHostHeartbeat()
    {
        if (!HostHeartbeatDue())
            return;
        if (Interlocked.CompareExchange(ref _hostHeartbeatBusy, 1, 0) != 0)
            return;

        var rcon = _localRcon;
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        _ = Task.Run(() =>
        {
            bool? alive = null;
            try
            {
                if (rcon is not null && ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root))
                    alive = rcon.Ping(TimeSpan.FromSeconds(1.5)).Ok;
            }
            catch
            {
                alive = null;
            }
            finally
            {
                Interlocked.Exchange(ref _hostHeartbeatBusy, 0);
            }
            if (alive is { } a && !_windowClosing)
                Dispatcher.BeginInvoke(() => NoteHostHeartbeat(a));
        });
    }

    /// <summary>
    /// The dedi answers RCON from its frame loop. Only asked once the host
    /// is ready and outside level loads, where long frames are normal.
    /// </summary>
    private bool HostHeartbeatDue()
    {
        if (_windowClosing || !_hostedLocalMatch || _localRcon is null)
            return false;
        if (_changeMapBusy || _pendingChangeMap is not null)
            return false;
        if (Volatile.Read(ref _crashPromptPosted) != 0)
            return false;
        var now = DateTime.UtcNow;
        if (now < _hostHeartbeatGraceUntilUtc || now < _hostHeartbeatNextUtc)
            return false;
        _hostHeartbeatNextUtc = now + HostHeartbeatEvery;
        return true;
    }

    private void NoteHostHeartbeat(bool alive)
    {
        if (alive)
        {
            _hostHeartbeatMisses = 0;
            return;
        }

        _hostHeartbeatMisses++;
        var now = DateTime.UtcNow;
        if (now - _hostHeartbeatLastLogUtc > TimeSpan.FromSeconds(10))
        {
            _hostHeartbeatLastLogUtc = now;
            Log($"Play Local: server not answering RCON ({_hostHeartbeatMisses}/{HostHeartbeatMissLimit})");
        }
        if (_hostHeartbeatMisses < HostHeartbeatMissLimit)
            return;

        _pendingFault = HostedServerFault.Hung;
        _pendingCrashExcerpt = RecentScriptError();
        QueueHostedServerCrash();
    }

    private string? RecentScriptError() =>
        _lastScriptError is not null && DateTime.UtcNow - _lastScriptErrorUtc < TimeSpan.FromMinutes(2)
            ? _lastScriptError
            : null;

    /// <summary>The level load stalls the frame loop; pause the heartbeat until the host is back.</summary>
    private void NoteHostLevelLoad()
    {
        if (_hostHeartbeatGraceUntilUtc != DateTime.MaxValue)
            _hostHeartbeatGraceUntilUtc = DateTime.UtcNow + HostHeartbeatGrace;
        _hostHeartbeatMisses = 0;
    }

    private void NoteHostLevelReady()
    {
        if (_hostHeartbeatGraceUntilUtc != DateTime.MaxValue)
            _hostHeartbeatGraceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        _hostHeartbeatMisses = 0;
    }

    /// <summary>
    /// The client just showed its own disconnect dialog. Tell the player
    /// what happened on the server side: dead, stuck, or still fine.
    /// </summary>
    private void OnHostedClientLostServer(string message)
    {
        if (_windowClosing || !_hostedLocalMatch || _changeMapBusy || _pendingChangeMap is not null)
            return;
        if (Volatile.Read(ref _crashPromptPosted) != 0)
            return;

        var root = TxtInstallRoot.Text.Trim();
        var rcon = _localRcon;
        _ = Task.Run(() =>
        {
            var dediAlive = ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root);
            var answers = dediAlive && rcon is not null && rcon.Ping(TimeSpan.FromSeconds(3)).Ok;
            Dispatcher.BeginInvoke(() =>
            {
                if (_windowClosing || !_hostedLocalMatch || Volatile.Read(ref _crashPromptPosted) != 0)
                    return;
                _pendingClientMessage = message;
                _pendingCrashExcerpt = RecentScriptError();
                _pendingFault = !dediAlive
                    ? HostedServerFault.Crashed
                    : answers ? HostedServerFault.ClientLost : HostedServerFault.Hung;
                Log($"Play Local: client lost the server ('{message}') dedi alive={dediAlive} rcon={answers}");
                QueueHostedServerCrash();
            });
        });
    }

    private void DisarmHostedMatch()
    {
        _hostedLocalMatch = false;
        _hostedDediSeen = false;
        _hostedClientSeen = false;
        _clientGoneArmed = false;
        _playLocalOwnsClient = false;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _windowClosing = true;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }

        if (_installBusy)
        {
            var ans = MessageBox.Show(
                this,
                Loc.Get("close_download"),
                Loc.Get("title_app"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes)
            {
                _windowClosing = false;
                e.Cancel = true;
                return;
            }

            _installRun?.Resume();
            _installCts?.Cancel();
            SevenZipLocator.KillOurUnpackers();
        }

        var root = TxtInstallRoot.Text.Trim();
        var clientAlive = ProcessSpawner.IsRoleAlive(LaunchRole.Client, root);
        var dediAlive = ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root);

        // Incomplete Play Local (map still loading) -- just abort the launch.
        if (_quickPlayBusy && _hostedLocalMatch)
        {
            _playCts?.Cancel();
            DisarmHostedMatch();
            ProcessSpawner.KillAll(root);
        }
        else if ((_hostedLocalMatch && (clientAlive || dediAlive)) ||
                 (_playLocalOwnsClient && clientAlive))
        {
            var hosted = _hostedLocalMatch;
            var ans = MessageBox.Show(
                this,
                hosted
                    ? Loc.Get("close_game_hosted")
                    : Loc.Get("close_game_client"),
                Loc.Get("title_app"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                hosted ? MessageBoxResult.Yes : MessageBoxResult.No);
            if (ans == MessageBoxResult.Cancel)
            {
                _windowClosing = false;
                e.Cancel = true;
                return;
            }

            if (ans == MessageBoxResult.Yes)
            {
                _playCts?.Cancel();
                if (hosted)
                    ProcessSpawner.KillAll(root);
                else
                    ProcessSpawner.KillRole(LaunchRole.Client, root);
            }
        }

        _playCts?.Cancel();
        DisarmHostedMatch();
        DropLocalRcon();
        DropHostedConsoles();
        StopWatchdogs();
        RestoreSessionMods(force: true);
        DisposeModsServices();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Loc.LanguageChanged -= OnLocLanguageChanged;
        _windowClosing = true;
        StopWatchdogs();
        DropHostedConsoles();
        DropLocalRcon();
        // Update.exe waits for this process to go away, then swaps the files in.
        ShellSelfUpdate.ApplyOnExit(Log);
        if (Application.Current is { } app)
            app.Shutdown();
    }

    private void StopWatchdogs()
    {
        _procWatch?.Stop();
        _shellUpdateWatch?.Stop();
        _consoleTimer?.Stop();
    }

    private void WatchHostedMatch()
    {
        var root = TxtInstallRoot.Text.Trim();
        WatchHostedMatch(ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root));
    }

    private void WatchHostedMatch(bool dediAlive)
    {
        if (_windowClosing || !_hostedLocalMatch || Volatile.Read(ref _crashPromptPosted) != 0)
            return;

        if (dediAlive)
        {
            _hostedDediSeen = true;
            return;
        }

        if (!_hostedDediSeen)
            return;

        QueueHostedServerCrash();
    }

    /// <summary>
    /// Local play owns both hooked processes. Client exit leaves the dedi
    /// running unless the user says to stop it.
    /// </summary>
    private void WatchHostedClientGone()
    {
        var root = TxtInstallRoot.Text.Trim();
        WatchHostedClientGone(
            ProcessSpawner.IsRoleAlive(LaunchRole.Client, root),
            ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root));
    }

    private void WatchHostedClientGone(bool clientAlive, bool dediAlive)
    {
        if (_windowClosing || _handlingClientGone || _handlingServerCrash
            || _quickPlayBusy || _restartClientBusy
            || Volatile.Read(ref _crashPromptPosted) != 0)
            return;

        // Only Play Local owns the pair. A leftover dedi from Advanced
        // Launch Dedi, or one left behind after Join, is not our match.
        if (!_hostedLocalMatch)
            return;

        if (clientAlive)
        {
            _hostedClientSeen = true;
            _clientGoneArmed = false;
            return;
        }

        if (!_hostedClientSeen || !dediAlive)
        {
            _clientGoneArmed = false;
            return;
        }

        // Loader relaunch drops the old PID for one beat. Confirm next tick.
        if (!_clientGoneArmed)
        {
            _clientGoneArmed = true;
            return;
        }

        OnHostedClientClosed();
    }

    private void OnHostedClientClosed()
    {
        if (_windowClosing || _handlingClientGone)
            return;
        _handlingClientGone = true;
        _hostedClientSeen = false;
        _clientGoneArmed = false;

        var root = TxtInstallRoot.Text.Trim();
        var ans = MessageBox.Show(
            this,
            Loc.Get("close_stop_dedi"),
            Loc.Get("title_app"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (ans == MessageBoxResult.Yes)
        {
            DisarmHostedMatch();
            var n = ProcessSpawner.KillRole(LaunchRole.Dedicated, root);
            _lastDediPid = null;
            DropLocalRcon();
            DropHostedConsoles();
            Log("Play Local: client closed — stopped dedi (" + n + ").");
            UpdateStatus(Loc.Get("status_game_closed_server_stopped"));
            SetSimpleStatus(Loc.Get("status_game_closed_server_stopped"));
        }
        else
        {
            Log("Play Local: client closed — dedi left running.");
            UpdateStatus(Loc.Get("status_game_closed_server_running"));
            SetSimpleStatus(Loc.Get("status_game_closed_server_running"));
        }

        UpdateKillButtons();
        _handlingClientGone = false;
    }

    /// <summary>
    /// One prompt per crash. Posted from the dedi console thread or the
    /// process watchdog; the dialog itself always runs on the UI thread.
    /// </summary>
    private void QueueHostedServerCrash()
    {
        if (_windowClosing || !_hostedLocalMatch)
            return;
        if (Interlocked.CompareExchange(ref _crashPromptPosted, 1, 0) != 0)
            return;

        _handlingServerCrash = true;
        _playCts?.Cancel();
        if (Dispatcher.CheckAccess())
            OnHostedServerCrashed();
        else
            Dispatcher.BeginInvoke(OnHostedServerCrashed);
    }

    private void EndPlayBusy()
    {
        _quickPlayBusy = false;
        if (BtnQuickPlay is not null)
            BtnQuickPlay.IsEnabled = true;
        UpdateKillButtons();
        RefreshSimplePlayButton();
    }

    private async Task<(HostReadyWait wait, bool unsignaled)> WaitHostReadyAsync(
        Process dedi, CancellationToken ct, bool simple)
    {
        var gate = _hostReady;
        if (gate is null)
            return (HostReadyWait.Cancelled, false);

        var startedUtc = DateTime.UtcNow;
        var waitTask = Task.Run(() => gate.Wait(dedi, ct), ct);
        var unsignaled = false;
        while (!waitTask.IsCompleted)
        {
            var sec = Math.Max(0, (int)(DateTime.UtcNow - startedUtc).TotalSeconds);
            if (sec >= HostReadySpawnAnywaySeconds)
            {
                unsignaled = true;
                break;
            }

            UpdateStatus($"Quick Play: waiting for the server ({sec}s)…");
            if (simple)
                SetSimpleStatus(Loc.Get("status_waiting_server"));
            var tick = await Task.WhenAny(waitTask, Task.Delay(1000, ct)).ConfigureAwait(true);
            if (tick == waitTask)
                break;
        }

        var wait = unsignaled
            ? HostReadyWait.Ready
            : await waitTask.ConfigureAwait(true);
        return (wait, unsignaled);
    }

    /// <summary>
    /// Play Local owns the dedi. A script error or a dead process kicks the
    /// client; warn, drop the dead server, and leave the game open.
    /// </summary>
    private void OnHostedServerCrashed()
    {
        if (_windowClosing)
            return;
        _handlingServerCrash = true;

        var excerpt = _pendingCrashExcerpt;
        var fault = _pendingFault;
        var clientMessage = _pendingClientMessage;
        var root = TxtInstallRoot.Text.Trim();
        var clientWasUp = ProcessSpawner.IsRoleAlive(LaunchRole.Client, root);

        ApplySimpleTab(SimpleTab.Console);
        var statusKey = fault switch
        {
            HostedServerFault.Hung => "status_server_hung",
            HostedServerFault.ClientLost => "status_client_lost_server",
            _ => "status_server_crashed",
        };
        UpdateStatus(Loc.Get(statusKey));

        string body;
        if (fault == HostedServerFault.ClientLost)
        {
            Log("Play Local: client dropped, server still answers, left it running.");
            SetSimpleStatus(Loc.Get(statusKey));
            _handlingServerCrash = false;
            _pendingCrashExcerpt = null;
            _pendingClientMessage = null;
            Interlocked.Exchange(ref _crashPromptPosted, 0);
            body = Loc.Format("warn_client_lost_server", clientMessage ?? string.Empty);
        }
        else
        {
            DisarmHostedMatch();
            var n = ProcessSpawner.KillRole(LaunchRole.Dedicated, root);
            _lastDediPid = null;
            DropLocalRcon();
            Log("Play Local: server " + (fault == HostedServerFault.Hung ? "hang" : "crash")
                + ", warned, killed dedi " + n + ".");
            SetSimpleStatus(clientWasUp
                ? Loc.Get("status_crash_client_running")
                : Loc.Get(statusKey));
            _handlingServerCrash = false;
            _pendingCrashExcerpt = null;
            _pendingClientMessage = null;
            EndPlayBusy();

            body = fault == HostedServerFault.Hung
                ? string.IsNullOrEmpty(excerpt)
                    ? Loc.Get("warn_server_hung")
                    : Loc.Format("warn_server_hung_script", excerpt)
                : string.IsNullOrEmpty(excerpt)
                    ? Loc.Get("warn_server_crashed")
                    : Loc.Format("warn_server_crashed_script", excerpt);
        }

        var wasTop = Topmost;
        Topmost = true;
        Activate();
        try
        {
            MessageBox.Show(
                this,
                body,
                Loc.Get("title_app"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                MessageBoxResult.OK);
        }
        finally
        {
            Topmost = wasTop;
        }
    }

    /// <summary>Show Kill only when that role has a live process under install path.</summary>
    private void UpdateKillButtons()
    {
        var root = TxtInstallRoot?.Text?.Trim() ?? string.Empty;
        var peek = ProcessSpawner.PeekRoles(root);
        UpdateKillButtons(peek.client, peek.dedi);
    }

    private void UpdateKillButtons(bool clientAlive, bool dediAlive)
    {
        if (BtnKillClient is null || BtnKillDedi is null)
            return;

        var root = TxtInstallRoot.Text.Trim();

        BtnKillClient.Visibility = clientAlive ? Visibility.Visible : Visibility.Collapsed;
        BtnKillDedi.Visibility = dediAlive ? Visibility.Visible : Visibility.Collapsed;

        var cl = ProcessSpawner.GetTrackedPids(LaunchRole.Client);
        var sv = ProcessSpawner.GetTrackedPids(LaunchRole.Dedicated);
        _lastClientPid = cl.Count > 0 ? cl[^1] : (clientAlive ? _lastClientPid : null);
        _lastDediPid = sv.Count > 0 ? sv[^1] : (dediAlive ? _lastDediPid : null);
        if (!clientAlive)
        {
            _lastClientPid = null;
            if (!_restartClientBusy)
                _launcherOwnsClient = false;
        }
        if (!dediAlive)
        {
            _lastDediPid = null;
            DropLocalRcon();
        }

        RefreshSimplePlayButton();
    }

    private string PidLine() =>
        $"client={_lastClientPid?.ToString() ?? "-"} dedi={_lastDediPid?.ToString() ?? "-"}";

    private void Log(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(line));
            return;
        }

        if (TxtLog is null)
            return;
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
        LauncherLog.Write(TxtInstallRoot?.Text ?? _settings.InstallPath, line);
    }

    private void SetError(string error) =>
        UpdateStatus($"ERROR: {error}  |  {PidLine()}");

    private void UpdateStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateStatus(text));
            return;
        }

        if (TxtStatus is not null)
            TxtStatus.Text = text;
    }

    // ---- Simple mode shell ----

    private void OnOpenGitHub(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.GitHubUrl);

    private void OnOpenDiscord(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.DiscordUrl);

    private void OnOpenWebsite(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.WebsiteUrl);

    private void OnCreditRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
            OpenExternal(url);
        e.Handled = true;
    }

    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    void ApplyDediPackageVisibility()
    {
        var show = CachedDownloadGate().Dedi
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (RowDediPackage is not null)
            RowDediPackage.Visibility = show;
        if (CardDediPackage is not null)
            CardDediPackage.Visibility = show;
    }

    private void OnOpenDediPackage(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.DediPackageUrl);

    private void OnOpenToolsRepos(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.ToolsReposUrl);

    private void OnOpenPatreon(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.PatreonUrl);

    private void OnOpenKofi(object sender, RoutedEventArgs e) =>
        OpenExternal(ProductConstants.KofiUrl);

    private void OnModeToggle(object sender, RoutedEventArgs e)
    {
        if (_settings.SimpleMode && TabBlockedBySetup())
            return;
        ApplyShellMode(simple: !_settings.SimpleMode, persist: true);
    }

    private void ApplyShellMode(bool simple, bool persist)
    {
        _settings.SimpleMode = simple;

        if (PanelSimple is not null)
            PanelSimple.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        if (PanelAdvanced is not null)
            PanelAdvanced.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        if (BarAdvancedStatus is not null)
            BarAdvancedStatus.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        if (BtnQuickPlay is not null)
            BtnQuickPlay.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        // Icon-only now: the state reads through the tooltip, not the label.
        if (BtnModeToggle is not null)
            BtnModeToggle.ToolTip = simple
                ? Loc.Get("tip_mode_advanced")
                : Loc.Get("tip_mode_simple");
        if (BtnTabToolAdvanced is not null)
            BtnTabToolAdvanced.ToolTip = Loc.Get("tip_mode_advanced");
        if (BtnHeaderConsole is not null)
            BtnHeaderConsole.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        // The simple shell shows these in its tab strip instead.
        var headerTools = simple ? Visibility.Collapsed : Visibility.Visible;
        if (BtnModeToggle is not null)
            BtnModeToggle.Visibility = headerTools;
        if (BtnTabSettings is not null)
            BtnTabSettings.Visibility = headerTools;
        if (BtnHeaderOpenFolder is not null)
            BtnHeaderOpenFolder.Visibility = headerTools;
        RefreshHeaderSubtitle();

        if (simple)
            SyncSimpleLaunchOptions();

        if (persist && IsLoaded)
        {
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        }

        if (!simple)
        {
            if (IsLoaded)
                _ = OpenServersAsync();
        }
        else
        {
            ApplySimpleTab(SimpleTab.Play);
        }

        RefreshSimplePlayButton();
    }

    private void RebuildModeCards()
    {
        if (ListModes is null)
            return;

        var keepId = _selectedMode?.Id ?? _settings.LastModePlaylist;
        _modeCards.Clear();
        _selectedMode = null;

        foreach (var family in _catalog.Families)
        {
            _settings.ModeMaps.TryGetValue(family.Id, out var remembered);
            var card = ModeCardViewModel.FromFamily(family, _catalog.MapNames, remembered);
            if (card.Maps.Count == 0)
                continue;
            _modeCards.Add(card);
        }

        if (!_modeCards.Any(c =>
                ModeCardViewModel.IsLobbyPlaylist(c.Id) ||
                (c.MapIsPinned && IsLobbyStem(c.SelectedMapStem))))
        {
            var lobbyMap = new MapOption(
                ModeCardViewModel.LobbyMapStem,
                MapOption.PlayerName(ModeCardViewModel.LobbyMapStem, _catalog.MapNames),
                ModeCardViewModel.LobbyLaunchPlaylist);
            _modeCards.Add(new ModeCardViewModel(
                ModeCardViewModel.LobbyPlaylistId,
                ModeGroups.Apex,
                ModeTitle(ModeCardViewModel.LobbyPlaylistId, "Lobby"),
                ModeBlurb(ModeCardViewModel.LobbyPlaylistId, "S21 offline lobby."),
                new[] { lobbyMap },
                lobbyMap,
                mapIsPinned: true,
                fallbackPlaylistId: ModeCardViewModel.LobbyLaunchPlaylist));
        }

        SortModeCardsByGroup();

        var grouped = new CollectionViewSource { Source = _modeCards };
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModeCardViewModel.GroupTitle)));
        ListModes.ItemsSource = null;
        ListModes.ItemsSource = grouped.View;

        if (_modeCards.Count == 0)
        {
            SetSimpleStatus(Loc.Get("status_no_modes"));
            RefreshHeroChrome();
            ShowLoadscreen(null, null);
            RefreshSimplePlayButton();
            return;
        }

        ModeCardViewModel? pick = null;
        if (!string.IsNullOrWhiteSpace(keepId))
        {
            pick = _modeCards.FirstOrDefault(c =>
                       string.Equals(c.Id, keepId, StringComparison.OrdinalIgnoreCase))
                   ?? _modeCards.FirstOrDefault(c =>
                       string.Equals(c.PlaylistId, keepId, StringComparison.OrdinalIgnoreCase));
        }
        pick ??= _modeCards.FirstOrDefault(c => ModeCardViewModel.IsLobbyPlaylist(c.Id))
                 ?? _modeCards[0];
        SelectModeCard(pick, persist: false);
        if (string.IsNullOrWhiteSpace(TxtSimpleStatus?.Text) ||
            string.Equals(TxtSimpleStatus.Text, Loc.Get("status_no_modes"), StringComparison.Ordinal))
            SetSimpleStatus(Loc.Get("ready_to_play"));
        RefreshSimplePlayButton();
    }

    private void SelectModeCard(ModeCardViewModel card, bool persist)
    {
        var previousStem = _selectedMode?.SelectedMapStem;
        foreach (var c in _modeCards)
            c.IsSelected = ReferenceEquals(c, card);
        _selectedMode = card;
        _settings.LastModePlaylist = card.Id;
        KeepMapIfInPlaylist(card, previousStem);
        BindSimpleMapPicker(card);
        RefreshHeroChrome();
        QueueLoadscreen();
        if (persist)
            PersistSettingsFromUi();
        RefreshChangeMapButton();
    }

    // Pinned modes (Lobby, Firing Range) keep their own map.
    private static void KeepMapIfInPlaylist(ModeCardViewModel card, string? previousStem)
    {
        if (card.MapIsPinned || string.IsNullOrWhiteSpace(previousStem))
            return;
        var match = card.Maps.FirstOrDefault(m =>
            string.Equals(m.Stem, previousStem, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            card.SelectedMap = match;
    }

    private void OnModeCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModeCardViewModel card })
            SelectModeCard(card, persist: true);
    }

    private void OnModeMapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist)
            return;
        if (sender is ComboBox { DataContext: ModeCardViewModel card })
        {
            if (!card.IsSelected)
                SelectModeCard(card, persist: false);
            PersistSettingsFromUi();
        }
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        switch (_simplePlayKind)
        {
            case SimplePlayKind.Stop:
                await StopSessionAsync().ConfigureAwait(true);
                break;
            case SimplePlayKind.ChangeMap:
                OnChangeMap(sender, e);
                break;
            case SimplePlayKind.RestartClient:
                await RestartClientForRendererAsync().ConfigureAwait(true);
                break;
            case SimplePlayKind.Disconnect:
                OnDisconnectClick(sender, e);
                break;
            case SimplePlayKind.Install:
            case SimplePlayKind.SetUp:
                _ = RunInstallAsync(InstallMode.Full);
                break;
            case SimplePlayKind.Repair:
                _ = RunRepairAsync(resumeIncomplete: true);
                break;
            case SimplePlayKind.Update:
                _ = RunInstallAsync(InstallMode.Full);
                break;
            case SimplePlayKind.SwitchAdvanced:
                ApplyShellMode(simple: false, persist: true);
                break;
            case SimplePlayKind.Installing:
                break;
            case SimplePlayKind.Play:
            default:
                {
                    // The caption can predate a folder that was deleted underneath us.
                    if (NeedsSetup(ReadInstallPathBox()))
                    {
                        InvalidateHealthMemo();
                        ShowSimpleSetupLayout(true);
                        RefreshSimplePlayButton();
                        SetSimpleStatus(Loc.Get("status_install_first"));
                        return;
                    }
                    if (_selectedMode is null)
                    {
                        SetSimpleStatus(Loc.Get("status_pick_mode"));
                        return;
                    }
                    var map = _selectedMode.SelectedMapStem;
                    if (string.IsNullOrWhiteSpace(map))
                    {
                        SetSimpleStatus(Loc.Get("status_pick_map"));
                        return;
                    }
                    await RunPlayAsync(_selectedMode.PlaylistId, map).ConfigureAwait(true);
                    break;
                }
        }
    }

    private async Task StopSessionAsync()
    {
        _playCts?.Cancel();
        DisarmHostedMatch();
        var root = TxtInstallRoot.Text.Trim();
        var n = await Task.Run(() => ProcessSpawner.KillAll(root)).ConfigureAwait(true);
        _lastClientPid = null;
        _lastDediPid = null;
        _joinedServer = null;
        RestoreSessionMods(force: true);
        DropLocalRcon();
        DropHostedConsoles();
        RefreshConnectionChrome();
        Log($"Simple STOP: killed {n} process(es)");
        UpdateKillButtons();
        SetSimpleStatus(Loc.Get("status_stopped"));
        RefreshSimplePlayButton();
    }

    /// <summary>
    /// While a session is up the one big button carries the action that fits it:
    /// CHANGE MAP when a local server can be steered somewhere new, DISCONNECT
    /// when the client is out on someone else's server, STOP otherwise.
    /// </summary>
    private void ApplyLiveSessionButton(bool dediAlive)
    {
        if (_restartClientBusy || RendererRestartPending())
        {
            _simplePlayKind = SimplePlayKind.RestartClient;
            PaintPlayButton(Loc.Get("restart_game_action"), enabled: !_restartClientBusy, stop: false);
            if (BtnPlay is not null)
                BtnPlay.ToolTip = _settings.UseDx12
                    ? Loc.Get("tip_restart_game_dx12")
                    : Loc.Get("tip_restart_game_dx11");
            return;
        }

        if (dediAlive && ChangeMapPending())
        {
            _simplePlayKind = SimplePlayKind.ChangeMap;
            PaintPlayButton(ChangeActionCaption(), enabled: !_changeMapBusy, stop: false);
            if (BtnPlay is not null)
                BtnPlay.ToolTip = ChangeActionTip();
            return;
        }

        if (!dediAlive && _joinedServer is not null && ClientIsSteerable())
        {
            _simplePlayKind = SimplePlayKind.Disconnect;
            PaintPlayButton(Loc.Get("disconnect_action"), enabled: !_steerBusy, stop: false);
            if (BtnPlay is not null)
                BtnPlay.ToolTip = Loc.Format("tip_disconnect_from", _joinedServer.Name);
            return;
        }

        _simplePlayKind = SimplePlayKind.Stop;
        PaintPlayButton(Loc.Get("stop"), enabled: true, stop: true);
        if (BtnPlay is not null)
            BtnPlay.ToolTip = Loc.Get("tip_stop");
    }

    /// <summary>
    /// Repaint the big button in place when only the live-session facts moved
    /// (map selection, join state). Cheap, and never re-enters the full refresh.
    /// </summary>
    private void RefreshLiveSessionButton()
    {
        if (!SessionLive || BtnPlay is null)
            return;
        ApplyLiveSessionButton(LocalDediAlive());
        RefreshConsoleChrome();
        PaintQuickPlayButton();
    }

    /// <summary>
    /// Map install health + process liveness to the Simple PLAY button caption/action.
    /// Called from the same paths that refresh Advanced labels and kill buttons.
    /// </summary>
    private void RefreshSimplePlayButton(InstallHealthReport? health = null)
    {
        if (BtnPlay is null)
        {
            RefreshChangeMapButton();
            return;
        }

        try
        {
            var root = ReadInstallPathBox();
            health ??= HealthForUi(root);
            var confirmed = GameConfirmed(root, health);

            if (_verifyBusy || _installBusy || _quickPlayBusy || _joinBusy)
            {
                if (_verifyBusy)
                {
                    _simplePlayKind = SimplePlayKind.Installing;
                    PaintPlayButton(Loc.Get("checking"), enabled: false, stop: false);
                    ShowSimpleSetupLayout(true);
                }
                else if (_installBusy)
                {
                    _simplePlayKind = SimplePlayKind.Installing;
                    PaintPlayButton(Loc.Get(_contentRepair ? "repairing" : "installing"), enabled: false, stop: false);
                    ShowSimpleSetupLayout(!confirmed || _contentRepair);
                }
                else if (_quickPlayBusy && (_hostedLocalMatch ||
                    ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root)))
                {
                    _simplePlayKind = SimplePlayKind.Stop;
                    PaintPlayButton(Loc.Get("stop"), enabled: true, stop: true);
                    ShowSimpleSetupLayout(false);
                }
                else
                {
                    _simplePlayKind = SimplePlayKind.Play;
                    PaintPlayButton(Loc.Get("play"), enabled: false, stop: false);
                    ShowSimpleSetupLayout(false);
                }
                BtnPlay.Visibility = Visibility.Visible;
                return;
            }

            if (!confirmed)
            {
                var repair = !NeedsSetup(root);
                _simplePlayKind = repair ? SimplePlayKind.Repair : SimplePlayKind.Install;
                RefreshSimpleInstallCopy();
                PaintPlayButton(
                    Loc.Get(repair ? "repair_action" : "install"),
                    enabled: !_installDiskBlocked, stop: false);
                ShowSimpleSetupLayout(true);
                PaintInstallControls();
                ApplyInstallDiskStatus();
                if (repair && !_installDiskBlocked)
                    SetSimpleStatus(Loc.Get("status_files_need_repair"));
                return;
            }

            ShowSimpleSetupLayout(false);

            if (_modeCards.Count == 0 && _simpleTab == SimpleTab.Play)
            {
                _simplePlayKind = SimplePlayKind.SwitchAdvanced;
                PaintPlayButton(Loc.Get("advanced"), enabled: true, stop: false);
                return;
            }

            var clientAlive = ProcessSpawner.IsRoleAlive(LaunchRole.Client, root);
            var dediAlive = ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root);
            if (clientAlive || dediAlive)
            {
                ApplyLiveSessionButton(dediAlive);
                return;
            }

            if (health.Enforced)
            {
                if (health.NeedsUpdate &&
                    LaneBlocksPlay(health, CachedDownloadGate(), requireClient: true, requireServer: true))
                {
                    _simplePlayKind = SimplePlayKind.Update;
                    RefreshSimpleInstallCopy();
                    PaintPlayButton(Loc.Get("update"), enabled: !_installDiskBlocked, stop: false);
                    if (_installDiskBlocked)
                        ApplyInstallDiskStatus();
                    else
                        SetSimpleStatus(Loc.Get("status_update_available"));
                    return;
                }

                if (health.NeedsUpdate)
                    SetSimpleStatus(Loc.Get("status_play_downloads_off"));

                if ((health.BlocksPlay && !health.NeedsUpdate) || health.NeedsRepair)
                {
                    var repair = !NeedsSetup(root);
                    _simplePlayKind = repair ? SimplePlayKind.Repair : SimplePlayKind.Install;
                    PaintPlayButton(Loc.Get(repair ? "repair_action" : "install"), enabled: true, stop: false);
                    return;
                }
            }

            _simplePlayKind = SimplePlayKind.Play;
            PaintPlayButton(Loc.Get("play"), enabled: _selectedMode is not null, stop: false);
            BtnPlay.Visibility = _simpleTab == SimpleTab.Play ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            RefreshChangeMapButton();
            RefreshConsoleChrome();
            PaintQuickPlayButton();
        }
    }

    void PaintPlayButton(string caption, bool enabled, bool stop)
    {
        var needDisk = _simplePlayKind is SimplePlayKind.Install
            or SimplePlayKind.Repair
            or SimplePlayKind.Update
            or SimplePlayKind.SetUp;
        if (needDisk && _installDiskBlocked)
            enabled = false;

        if (_playPainted
            && _lastPlayCaption == caption
            && _lastPlayEnabled == enabled
            && _lastPlayStop == stop
            && _lastPaintKind == _simplePlayKind)
            return;
        _playPainted = true;
        _lastPlayCaption = caption;
        _lastPlayEnabled = enabled;
        _lastPlayStop = stop;
        _lastPaintKind = _simplePlayKind;

        if (BtnPlay is not null)
        {
            BtnPlay.Content = caption;
            BtnPlay.IsEnabled = enabled;
            BtnPlay.Visibility = Visibility.Visible;
            BtnPlay.ToolTip = null;
            var styleKey = _simplePlayKind switch
            {
                SimplePlayKind.ChangeMap => "PlayBarChange",
                SimplePlayKind.RestartClient => "PlayBarRestart",
                SimplePlayKind.Disconnect => "PlayBarDisconnect",
                SimplePlayKind.Update => "PlayBarUpdate",
                _ => stop ? "PlayBarDanger" : "PlayBarButton",
            };
            if (BtnPlay.TryFindResource(styleKey) is Style style)
                BtnPlay.Style = style;
        }

        PaintConsoleActionButton(caption, enabled);
        PaintAdvancedClientButton();
        PaintDx12Tooltip();

        if (BtnSimpleSetupInstall is not null)
        {
            var setupCaption = _simplePlayKind switch
            {
                SimplePlayKind.Installing => caption,
                SimplePlayKind.Update => Loc.Get("update"),
                SimplePlayKind.Repair => Loc.Get("repair_action"),
                _ => Loc.Get("install"),
            };
            BtnSimpleSetupInstall.Content = setupCaption;
            BtnSimpleSetupInstall.IsEnabled = enabled &&
                (_simplePlayKind is SimplePlayKind.Install
                    or SimplePlayKind.Repair
                    or SimplePlayKind.Update
                    or SimplePlayKind.SetUp);
            var setupKey = _simplePlayKind == SimplePlayKind.Update
                ? "PlayBarUpdate"
                : "PlayBarButton";
            if (BtnSimpleSetupInstall.TryFindResource(setupKey) is Style setupStyle)
                BtnSimpleSetupInstall.Style = setupStyle;
        }
    }

    /// <summary>
    /// Advanced has no play bar, so the Client card's LAUNCH carries the restart:
    /// with the game already up on the wrong renderer, launching a second copy is
    /// never what the DX12 box was asking for.
    /// </summary>
    void PaintAdvancedClientButton()
    {
        if (BtnLaunchClient is null)
            return;

        var restart = _simplePlayKind == SimplePlayKind.RestartClient;
        BtnLaunchClient.Content = restart
            ? Loc.Get("restart_game_action")
            : Loc.Get("launch_client");
        BtnLaunchClient.IsEnabled = !_restartClientBusy;
        BtnLaunchClient.ToolTip = restart
            ? (_settings.UseDx12
                ? Loc.Get("tip_restart_game_dx12")
                : Loc.Get("tip_restart_game_dx11"))
            : null;
        if (BtnLaunchClient.TryFindResource(restart ? "RestartButton" : "AccentButton") is Style style)
            BtnLaunchClient.Style = style;
    }

    /// <summary>
    /// The Console tab's action button mirrors the big one in every state: PLAY,
    /// INSTALL, UPDATE, CHANGE MAP, RESTART GAME, DISCONNECT or STOP.
    /// </summary>
    void PaintConsoleActionButton(string caption, bool enabled)
    {
        if (BtnConsoleAction is null)
            return;

        string styleKey;
        string? tip;
        switch (_simplePlayKind)
        {
            case SimplePlayKind.Update:
                styleKey = "UpdateButtonSmall";
                tip = Loc.Get("tip_update");
                break;
            case SimplePlayKind.Play:
            case SimplePlayKind.Install:
            case SimplePlayKind.Repair:
            case SimplePlayKind.SetUp:
            case SimplePlayKind.Installing:
            case SimplePlayKind.SwitchAdvanced:
                styleKey = "AccentButton";
                tip = null;
                break;
            case SimplePlayKind.ChangeMap:
                styleKey = "ChangeButtonSmall";
                tip = ChangeActionTip();
                break;
            case SimplePlayKind.RestartClient:
                styleKey = "RestartButtonSmall";
                tip = _settings.UseDx12
                    ? Loc.Get("tip_restart_game_dx12")
                    : Loc.Get("tip_restart_game_dx11");
                break;
            case SimplePlayKind.Disconnect:
                styleKey = "LeaveButtonSmall";
                tip = _joinedServer is null
                    ? Loc.Get("tip_stop")
                    : Loc.Format("tip_disconnect_from", _joinedServer.Name);
                break;
            default:
                caption = Loc.Get("stop");
                enabled = true;
                styleKey = "DangerButtonSmall";
                tip = Loc.Get("tip_stop");
                break;
        }

        BtnConsoleAction.Content = caption;
        BtnConsoleAction.IsEnabled = enabled;
        BtnConsoleAction.ToolTip = tip;
        if (BtnConsoleAction.TryFindResource(styleKey) is Style style)
            BtnConsoleAction.Style = style;
    }

    void OnInstallPause(object sender, RoutedEventArgs e)
    {
        if (_installRun is null || !_installBusy)
            return;
        if (_installRun.IsPaused)
        {
            _installRun.Resume();
            SetSimpleStatus(Loc.Get("status_installing_game"));
            if (TxtSimpleProgressPhase is not null &&
                string.Equals(TxtSimpleProgressPhase.Text, Loc.Get("phase_paused"), StringComparison.Ordinal))
                TxtSimpleProgressPhase.Text = Loc.Get("phase_download");
            Log("Content install resumed.");
        }
        else
        {
            _installRun.Pause();
            SetSimpleStatus(Loc.Get("status_paused"));
            if (TxtSimpleProgressPhase is not null)
                TxtSimpleProgressPhase.Text = Loc.Get("phase_paused");
            if (TxtSimpleProgressRate is not null)
                TxtSimpleProgressRate.Text = "";
            Log("Content install paused.");
        }

        PaintInstallControls();
    }

    void OnInstallCancel(object sender, RoutedEventArgs e)
    {
        if (!_installBusy)
            return;
        _installRun?.Resume();
        _installCts?.Cancel();
        SetSimpleStatus(Loc.Get("status_cancelling"));
        Log("Content install cancel requested.");
        PaintInstallControls();
    }

    void PaintInstallControls()
    {
        var busy = _installBusy;
        var paused = _installRun?.IsPaused == true;
        if (TxtSimpleSetupTitle is not null)
            TxtSimpleSetupTitle.Text = busy
                ? Loc.Get(_contentRepair ? "repairing_the_game" : "installing_the_game")
                : _verifyBusy ? Loc.Get("checking_the_game")
                : Loc.Get(_simplePlayKind == SimplePlayKind.Repair ? "repair_the_game" : "install_the_game");
        if (TxtSimpleSetupKicker is not null)
            TxtSimpleSetupKicker.Text = busy || _verifyBusy
                ? Loc.Get("hang_tight")
                : Loc.Get(_simplePlayKind == SimplePlayKind.Repair ? "files_need_work" : "get_started");
        if (TxtSimpleSetupBlurb is not null)
            TxtSimpleSetupBlurb.Text = Loc.Get(
                _simplePlayKind == SimplePlayKind.Repair ? "repair_blurb" : "setup_blurb");
        // The setup panel keeps one bottom-anchored stack: the folder form and the
        // progress card swap in place, they never stack.
        if (PanelSimpleSetupIdle is not null)
            PanelSimpleSetupIdle.Visibility = busy || _verifyBusy
                ? Visibility.Collapsed
                : Visibility.Visible;
        if (BtnSimpleSetupInstall is not null)
            BtnSimpleSetupInstall.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (BtnSimpleInstallPause is not null)
        {
            BtnSimpleInstallPause.Content = paused ? Loc.Get("resume") : Loc.Get("pause");
            BtnSimpleInstallPause.IsEnabled = busy;
        }
        if (BtnSimpleInstallCancel is not null)
            BtnSimpleInstallCancel.IsEnabled = busy;
    }

    void KickShellSelfUpdate()
    {
        // A restart during a scan costs nothing; during a write it would
        // interrupt a rename, so only an install blocks self-update.
        _ = ShellSelfUpdate.TryAsync(
            ReadInstallPathBox(),
            () => _installBusy,
            Log,
            (state, ver) => Dispatcher.BeginInvoke(() => SetShellUpdateChrome(state, ver)));
    }

    void SetShellUpdateChrome(ShellSelfUpdate.State state, string? version)
    {
        _shellUpdateState = state;
        _shellUpdateVer = version;
        var text = state switch
        {
            ShellSelfUpdate.State.Available => Loc.Get("update_available"),
            ShellSelfUpdate.State.PendingRestart => Loc.Get("update_pending"),
            _ => "",
        };
        var show = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(version) && text.Length > 0)
            text += $" (v{version})";
        if (TxtShellUpdateLink is not null)
            TxtShellUpdateLink.Visibility = show;
        if (RunShellUpdateLink is not null)
            RunShellUpdateLink.Text = text;
        if (TxtShellUpdateLinkAdv is not null)
            TxtShellUpdateLinkAdv.Visibility = show;
        if (RunShellUpdateLinkAdv is not null)
            RunShellUpdateLinkAdv.Text = text;
    }

    void OnShellUpdateLinkClick(object sender, RoutedEventArgs e)
    {
        if (ShellSelfUpdate.TryApplyIfIdle(
                ReadInstallPathBox(),
                () => _installBusy,
                Log))
            return;
        SetSimpleStatus(Loc.Get("status_update_bg"));
        KickShellSelfUpdate();
    }

    /// <summary>
    /// A staged shell update waits for the player to close the launcher on their
    /// own terms. Pulling the window out from under them mid-session to install
    /// it is not worth the interruption; the Update link is the way to have it
    /// applied now.
    /// </summary>
    void TryApplyShellUpdate()
    {
        if (_windowClosing)
            return;
        ShellSelfUpdate.ApplyOnExit(Log);
    }

    private void DropLocalRcon()
    {
        _localRcon?.Dispose();
        _localRcon = null;
        _livePlaylist = null;
        _liveMap = null;
        ClearPendingChangeMap();
        RefreshChangeMapButton();
    }

    private bool TrySelectedChangePair(out string playlist, out string map)
    {
        if (_settings.SimpleMode && _selectedMode is not null)
        {
            playlist = _selectedMode.PlaylistId;
            map = _selectedMode.SelectedMapStem;
        }
        else
        {
            playlist = SelectedPlaylistId() ?? string.Empty;
            map = SelectedMap() ?? string.Empty;
        }

        if (ModeCardViewModel.IsLobbyPlaylist(playlist) || IsLobbyStem(map))
            return false;

        if (!LocalRcon.TryBuildSetMode(playlist, map, out _, out _))
            return false;

        // The curated list is what Simple mode offers on a card; Advanced picks
        // straight from the playlist defs and may steer anywhere they allow.
        if (_settings.SimpleMode)
        {
            var allowed = _catalog.MapChoicesForMode(playlist);
            if (allowed.Count > 0 &&
                !allowed.Contains(map, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private bool LocalDediAlive()
    {
        var root = TxtInstallRoot?.Text.Trim() ?? string.Empty;
        return ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root);
    }

    private bool ChangeMapPending()
    {
        if (_localRcon is null || _changeMapBusy)
            return false;
        if (!LocalDediAlive())
            return false;
        if (!TrySelectedChangePair(out var playlist, out var map))
            return false;
        if (string.Equals(playlist, _livePlaylist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(map, _liveMap, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// True when the pick keeps the live map and only moves the mode, so the
    /// action reads CHANGE PLAYLIST instead of CHANGE MAP.
    /// </summary>
    private bool PlaylistOnlyChangePending()
    {
        if (string.IsNullOrEmpty(_liveMap))
            return false;
        if (!TrySelectedChangePair(out var playlist, out var map))
            return false;
        return string.Equals(map, _liveMap, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(playlist, _livePlaylist, StringComparison.OrdinalIgnoreCase);
    }

    private string ChangeActionCaption() =>
        Loc.Get(PlaylistOnlyChangePending() ? "change_playlist_action" : "change_map_action");

    private string ChangeActionTip()
    {
        if (PlaylistOnlyChangePending())
        {
            TrySelectedChangePair(out var playlist, out _);
            var mode = PlaylistDisplayName(playlist);
            return string.IsNullOrWhiteSpace(mode)
                ? Loc.Get("tip_change_playlist")
                : Loc.Format("tip_change_playlist_to", mode);
        }

        var label = ChangeTargetMapLabel();
        return string.IsNullOrWhiteSpace(label)
            ? Loc.Get("tip_change_map")
            : Loc.Format("tip_change_map_to", label);
    }

    private string ChangeTargetMapLabel()
    {
        if (!TrySelectedChangePair(out _, out var map))
            return string.Empty;
        if (_settings.SimpleMode && _selectedMode?.SelectedMap is { } pick &&
            string.Equals(pick.Stem, map, StringComparison.OrdinalIgnoreCase))
            return pick.DisplayName;
        return MapDisplayName(map);
    }

    private string PlaylistDisplayName(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return string.Empty;
        foreach (var card in _modeCards)
        {
            if (string.Equals(card.PlaylistId, playlistId, StringComparison.OrdinalIgnoreCase))
                return card.Title;
        }
        foreach (var mode in _catalog.Modes)
        {
            if (string.Equals(mode.Id, playlistId, StringComparison.OrdinalIgnoreCase))
                return ModeTitle(mode.Id, mode.Title);
        }
        return playlistId;
    }

    /// <summary>
    /// The renderer is baked at spawn: r5apex.exe is DX11, r5apex_dx12.exe is DX12.
    /// Toggling the box under a live game changes nothing until that image is
    /// replaced, so the running image -- never the setting -- decides.
    /// </summary>
    private bool RendererRestartPending()
    {
        if (_restartClientBusy || _quickPlayBusy || _joinBusy || _installBusy || _verifyBusy)
            return false;
        if (!_launcherOwnsClient)
            return false;

        var root = ReadInstallPathBox();
        var live = ProcessSpawner.LiveClientRenderer(root);
        if (live == ProcessSpawner.ClientRenderer.None)
            return false;

        var want = _settings.UseDx12 && _dx12Available
            ? ProcessSpawner.ClientRenderer.Dx12
            : ProcessSpawner.ClientRenderer.Dx11;
        return live != want;
    }

    /// <summary>
    /// Put the game back up on the other renderer without disturbing the server:
    /// only the client image is replaced, and it reconnects where it stood. A map
    /// pick made in the same breath is left for the press after this one, so the
    /// reconnect never races a changelevel.
    /// </summary>
    private async Task RestartClientForRendererAsync()
    {
        if (_restartClientBusy)
            return;

        var root = TxtInstallRoot.Text.Trim();
        var joined = _joinedServer;
        var dediAlive = LocalDediAlive();

        _restartClientBusy = true;
        RefreshSimplePlayButton();
        try
        {
            var target = _settings.UseDx12 ? "DirectX 12" : "DirectX 11";
            Log($"Restart game: switching the client to {target}");
            SetSimpleStatus(Loc.Format("status_restarting_game", target));
            UpdateStatus("Restarting the game on " + target + "…");

            // Not our match while the client is deliberately down: the watchdog
            // would read the gap as the player closing the game.
            var wasHosted = _hostedLocalMatch;
            DisarmHostedMatch();

            var killed = await Task.Run(() => ProcessSpawner.KillRole(LaunchRole.Client, root))
                .ConfigureAwait(true);
            _lastClientPid = null;
            if (killed > 0)
                Log($"Restart game: killed {killed} client process(es)");
            ResetClientConsoleForNewSession();
            await Task.Delay(400).ConfigureAwait(true);

            if (!dediAlive && joined is not null)
            {
                // Relaunch, never hop: an in-place connect keeps the old image.
                await RunJoinAsync(joined, forceRelaunch: true).ConfigureAwait(true);
                if (!ProcessSpawner.IsRoleAlive(LaunchRole.Client, root))
                {
                    Log("Restart game: rejoin did not bring the client back");
                    SetSimpleStatus(Loc.Get("status_restart_game_failed"));
                }
                return;
            }

            var clientArgs = dediAlive
                ? BuildClientArgs(
                    includeConnect: true,
                    connectHost: "127.0.0.1",
                    connectPort: SelectedPort(),
                    includePassword: true,
                    noTimeout: true)
                : BuildClientArgs(includeConnect: false);
            var result = SpawnRoleResult(
                LaunchRole.Client, clientArgs,
                quiet: _settings.SimpleMode,
                grantHostConsole: dediAlive);
            if (wasHosted && LocalDediAlive())
                ArmHostedMatch();
            if (!result.Ok)
            {
                SetSimpleStatus(Loc.Get("status_restart_game_failed"));
                SetError(result.Error ?? "client spawn failed");
                return;
            }

            _lastClientPid = result.ProcessId;
            Log($"Restart game: client pid={result.ProcessId} on {target}");
            SetSimpleStatus(Loc.Format("status_restarted_game", target));
            UpdateStatus($"Restarted on {target}  |  {PidLine()}");
            MaybeOpenConsoleForLaunch();
        }
        catch (Exception ex)
        {
            Log("Restart game failed: " + ex.Message);
            SetSimpleStatus(Loc.Get("status_restart_game_failed"));
        }
        finally
        {
            _restartClientBusy = false;
            UpdateKillButtons();
            RefreshSimplePlayButton();
        }
    }

    private void RefreshChangeMapButton()
    {
        var playing = _localRcon is not null || LocalDediAlive();
        var canSend = ChangeMapPending();
        var playlistOnly = canSend && PlaylistOnlyChangePending();
        var tip = !playing
            ? "Press PLAY first."
            : _localRcon is null
                ? "Start PLAY from this launcher so it can talk to the server."
                : canSend
                    ? ChangeActionTip()
                    : "Pick a different mode or map, then press CHANGE MAP.";

        // Simple mode carries CHANGE MAP inside the Play and Console action buttons.
        if (BtnChangeMap is not null)
            BtnChangeMap.Visibility = Visibility.Collapsed;
        if (BtnChangeMapAdvanced is not null)
        {
            BtnChangeMapAdvanced.Visibility = playing && !_settings.SimpleMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnChangeMapAdvanced.Content =
                Loc.Get(playlistOnly ? "change_playlist" : "change_map");
            BtnChangeMapAdvanced.IsEnabled = canSend;
            BtnChangeMapAdvanced.ToolTip = tip;
        }

        RefreshLiveSessionButton();
    }

    private async void OnChangeMap(object sender, RoutedEventArgs e)
    {
        if (_localRcon is null || _changeMapBusy)
            return;
        if (!TrySelectedChangePair(out var playlist, out var map))
        {
            SetSimpleStatus(Loc.Get("status_change_map_pick"));
            return;
        }

        var playlistOnly = PlaylistOnlyChangePending();
        var label = ChangeTargetMapLabel();
        if (string.IsNullOrWhiteSpace(label))
            label = MapDisplayName(map);
        await ApplyModeAsync(playlist, map, label, reload: false, playlistOnly)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Console-tab Reload sends the live playlist+map through the same
    /// changelevel path. CHANGE MAP stays for a different pick.
    /// </summary>
    private async void OnReloadLevel(object sender, RoutedEventArgs e)
    {
        if (_localRcon is null || _changeMapBusy)
            return;

        string playlist;
        string map;
        if (!string.IsNullOrEmpty(_livePlaylist) && !string.IsNullOrEmpty(_liveMap))
        {
            playlist = _livePlaylist;
            map = _liveMap;
        }
        else if (!TrySelectedChangePair(out playlist, out map))
        {
            SetSimpleStatus(Loc.Get("status_reload_level_none"));
            return;
        }

        if (ModeCardViewModel.IsLobbyPlaylist(playlist) || IsLobbyStem(map))
        {
            SetSimpleStatus(Loc.Get("status_reload_level_none"));
            return;
        }

        if (!LocalRcon.TryBuildSetMode(playlist, map, out _, out _))
        {
            SetSimpleStatus(Loc.Get("status_reload_level_none"));
            return;
        }

        await ApplyModeAsync(playlist, map, MapDisplayName(map), reload: true)
            .ConfigureAwait(true);
    }

    private async Task ApplyModeAsync(string playlist, string map, string label, bool reload,
        bool playlistOnly = false)
    {
        if (_localRcon is null || _changeMapBusy)
            return;

        var modeLabel = PlaylistDisplayName(playlist);
        _changeMapBusy = true;
        _pendingChangeMap = map;
        _pendingChangeLabel = label;
        RefreshChangeMapButton();
        SetSimpleStatus(reload
            ? Loc.Format("status_reloading_level", label)
            : playlistOnly
                ? Loc.Format("status_changing_playlist", modeLabel)
                : Loc.Format("status_changing_map", label));
        UpdateStatus((reload
            ? "Reloading " + label
            : playlistOnly
                ? "Switching playlist to " + modeLabel
                : "Changing map to " + label) + "…");
        try
        {
            var session = _localRcon;
            var result = await Task.Run(() => session.SetMode(playlist, map)).ConfigureAwait(true);
            if (result.Ok)
            {
                _livePlaylist = playlist;
                _liveMap = map;
                Log((reload ? "Reload level: " : "Change map: ") + playlist + " " + map);
            }
            else
            {
                ClearPendingChangeMap();
                SetSimpleStatus(Loc.Get(reload
                    ? "status_reload_level_loading"
                    : playlistOnly
                        ? "status_change_playlist_loading"
                        : "status_change_map_loading"));
                UpdateStatus(reload
                    ? "Reload failed"
                    : playlistOnly ? "Playlist change failed" : "Map change failed");
                Log((reload ? "Reload level failed: " : "Change map failed: ")
                    + (result.Error ?? "unknown"));
            }
        }
        catch (Exception ex)
        {
            ClearPendingChangeMap();
            SetSimpleStatus(Loc.Get(reload
                ? "status_reload_level_failed"
                : playlistOnly
                    ? "status_change_playlist_failed"
                    : "status_change_map_failed"));
            Log((reload ? "Reload level failed: " : "Change map failed: ") + ex.Message);
        }
        finally
        {
            _changeMapBusy = false;
            RefreshChangeMapButton();
        }
    }

    private void ClearPendingChangeMap()
    {
        _pendingChangeMap = null;
        _pendingChangeLabel = null;
    }

    private string MapDisplayName(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return stem;
        return MapOption.PlayerName(stem, _catalog.MapNames);
    }

    private void NoteMapChangeReady()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(NoteMapChangeReady);
            return;
        }

        var map = _pendingChangeMap;
        if (string.IsNullOrEmpty(map))
            return;

        var label = _pendingChangeLabel;
        if (string.IsNullOrWhiteSpace(label))
            label = MapDisplayName(map);
        ClearPendingChangeMap();

        if (_restartClientBusy || _windowClosing)
            return;

        SetSimpleStatus(Loc.Format("status_playing_map", label));
        UpdateStatus("Playing  |  " + (_livePlaylist ?? "") + " " + map);
    }

    /// <summary>The rail groups on GroupTitle, so the source must already be in
    /// group order -- a CollectionViewSource group boundary is a run, not a key.</summary>
    private void SortModeCardsByGroup()
    {
        // Catalog order is already group -> family order -> title, and LINQ sorts
        // stably, so ranking the group alone keeps that and parks the synthetic
        // Lobby card at the end of the Apex run where it was appended.
        var ordered = _modeCards.OrderBy(c => c.GroupRank).ToList();
        _modeCards.Clear();
        foreach (var c in ordered)
            _modeCards.Add(c);
    }

    private void SetSimpleStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetSimpleStatus(text));
            return;
        }

        if (TxtSimpleStatus is not null)
            TxtSimpleStatus.Text = text;
        if (DotSimpleStatus is not null)
            DotSimpleStatus.Fill = BrushForStatus(text);
    }

    private Brush BrushForStatus(string text)
    {
        if (text.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("script error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Could not", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not have", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("need work", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Pick a", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Accept the", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Not enough", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("disk space", StringComparison.OrdinalIgnoreCase))
            return (Brush)FindResource("DangerLine");
        if (text.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Checking", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Installing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Opening", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Repair", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Joining", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Changing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("…", StringComparison.Ordinal))
            return (Brush)FindResource("PingFair");
        return (Brush)FindResource("Accent");
    }

    string ReadInstallPathBox()
    {
        if (TxtSimpleInstallRoot is not null &&
            PanelSimpleSetup?.Visibility == Visibility.Visible)
        {
            var simple = TxtSimpleInstallRoot.Text.Trim();
            if (!string.IsNullOrWhiteSpace(simple))
                return simple;
        }
        return TxtInstallRoot?.Text?.Trim() ?? string.Empty;
    }

    void SyncInstallPathBoxes(string path)
    {
        if (TxtInstallRoot is not null &&
            !string.Equals(TxtInstallRoot.Text, path, StringComparison.Ordinal))
            TxtInstallRoot.Text = path;
        if (TxtSimpleInstallRoot is not null &&
            !string.Equals(TxtSimpleInstallRoot.Text, path, StringComparison.Ordinal))
            TxtSimpleInstallRoot.Text = path;
        if (!string.Equals(_healthMemoRoot, path.Trim(), StringComparison.OrdinalIgnoreCase))
            InvalidateHealthMemo();
        KickHealthRefresh(path);
    }

    void ShowSimpleSetupLayout(bool setup)
    {
        if (PanelSimpleSetup is not null)
            PanelSimpleSetup.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        if (PanelSimplePlay is not null)
            PanelSimplePlay.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
        if (PanelSimplePlayOptions is not null)
            PanelSimplePlayOptions.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
        if (BarSimplePlay is not null)
            BarSimplePlay.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
        if (BtnTabLocal is not null)
            BtnTabLocal.Content = setup ? Loc.Get("tab_install") : Loc.Get("tab_play_local");
        if (BtnTabServers is not null)
        {
            BtnTabServers.IsEnabled = !setup;
            BtnTabServers.Opacity = setup ? 0.4 : 1;
            BtnTabServers.ToolTip = setup ? Loc.Get("tip_servers_locked") : Loc.Get("tip_browse_servers");
        }
        if (setup)
        {
            RefreshSimpleInstallCopy();
            ClearServerList();
        }
        ApplyInstallGates();
        RefreshHeaderSubtitle();
    }

    /// <summary>
    /// Everything past the install card reads or drives a game that is not there
    /// yet: the console tails its logs, mods write into it, settings edit its
    /// launch arguments, and Advanced launches it. Locked until the files exist.
    /// </summary>
    internal bool InstallGateLocked => NeedsSetup(ReadInstallPathBox());

    private bool _applyingGates;

    private void ApplyInstallGates()
    {
        if (_applyingGates)
            return;
        _applyingGates = true;
        try { ApplyInstallGatesCore(); }
        finally { _applyingGates = false; }
    }

    private void ApplyInstallGatesCore()
    {
        var locked = InstallGateLocked;
        var tip = locked ? Loc.Get("tip_install_first") : null;

        void Gate(Button? b, string? unlockedTip)
        {
            if (b is null)
                return;
            b.IsEnabled = !locked;
            b.Opacity = locked ? 0.4 : 1;
            b.ToolTip = locked ? tip : unlockedTip;
        }

        Gate(BtnTabConsole, null);
        Gate(BtnTabMods, null);
        Gate(BtnTabSettings, Loc.Get("tip_settings"));
        Gate(BtnTabToolSettings, Loc.Get("tip_settings"));
        Gate(BtnHeaderConsole, Loc.Get("tab_console"));
        Gate(BtnModeToggle, Loc.Get("tip_mode_advanced"));
        Gate(BtnTabToolAdvanced, Loc.Get("tip_mode_advanced"));

        if (!locked)
            return;

        if (!_settings.SimpleMode)
            ApplyShellMode(simple: true, persist: false);
        if (_simpleTab is SimpleTab.Console or SimpleTab.Mods or SimpleTab.Settings or SimpleTab.Servers)
            ApplySimpleTab(SimpleTab.Play);
    }

    /// <summary>Refuse a tab that needs an install, and say why once.</summary>
    private bool TabBlockedBySetup()
    {
        if (!InstallGateLocked)
            return false;
        ApplySimpleTab(SimpleTab.Play);
        SetSimpleStatus(Loc.Get("status_install_first"));
        return true;
    }

    /// <summary>The tab bar already names the open tab; the subtitle carries only what it cannot.</summary>
    void RefreshHeaderSubtitle()
    {
        if (TxtHeaderSubtitle is null)
            return;

        var label = _settings.SimpleMode
            ? _simpleTab == SimpleTab.Settings ? Loc.Get("header_settings") : null
            : _simpleTab switch
            {
                SimpleTab.Console => Loc.Get("header_console"),
                SimpleTab.Settings => Loc.Get("header_settings"),
                _ => Loc.Get("header_advanced"),
            };

        TxtHeaderSubtitle.Text = label is null ? string.Empty : "  ·  " + label;
    }

    void RefreshSimpleInstallCopy()
    {
        _installDiskBlocked = false;
        _installDiskMessage = "";

        if (TxtSimpleInstallSize is null)
            return;

        var root = ReadInstallPathBox();
        if (string.IsNullOrWhiteSpace(root))
            root = _settings.InstallPath;

        void PaintSize(string text, bool danger)
        {
            TxtSimpleInstallSize.Text = text;
            TxtSimpleInstallSize.SetResourceReference(
                TextBlock.ForegroundProperty,
                danger ? "DangerFg" : "TextSecondary");
        }

        if (_simplePlayKind == SimplePlayKind.Repair)
        {
            PaintSize(Loc.Get("disk_repair"), danger: false);
            return;
        }

        if (_manifest is null)
        {
            PaintSize(Loc.Get("disk_unknown"), danger: false);
            return;
        }

        if (!TryMeasureInstallDisk(InstallMode.Full, root, out var planned, out var need, out var free))
        {
            PaintSize(Loc.Get("disk_unread"), danger: false);
            return;
        }

        var size = FormatDownloadSize(planned);
        var needTxt = FormatDownloadSize(need);
        if (free >= 0 && free < need)
        {
            _installDiskBlocked = true;
            _installDiskMessage = FormatDiskShortage(need, planned, free);
            PaintSize(_installDiskMessage, danger: true);
            ApplyInstallDiskStatus();
            return;
        }

        if (free >= 0)
        {
            PaintSize(
                Loc.Format("disk_about", size, needTxt, FormatDownloadSize(free)),
                danger: false);
        }
        else
        {
            PaintSize(
                Loc.Format("disk_about_nodrive", size, needTxt),
                danger: false);
        }
    }

    void ApplyInstallDiskStatus()
    {
        if (_installDiskBlocked &&
            _installDiskMessage.Length > 0 &&
            !_verifyBusy &&
            !_installBusy)
            SetSimpleStatus(_installDiskMessage);
    }

    bool TryMeasureInstallDisk(InstallMode mode, string path, out long planned, out long need, out long free)
    {
        planned = 0;
        need = 0;
        free = -1;
        if (_manifest is null || string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var plan = InstallPlanner.Build(_manifest, mode, path);
            var gate = CachedDownloadGate();
            InstallPlanner.FilterDownloadLanes(plan, gate.Content, gate.Platform);
            InstallPlanner.ExcludeCurrentTracks(plan, _manifest, path);
            InstallPlanner.MeasureWork(plan, _manifest, out planned, out need);
            free = InstallPathPolicy.FreeBytes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string FormatDiskShortage(long need, long planned, long free)
    {
        var freeTxt = free <= 0 ? Loc.Get("disk_nospace") : FormatDownloadSize(free);
        return Loc.Format(
            "disk_shortage",
            FormatDownloadSize(need),
            FormatDownloadSize(planned),
            freeTxt);
    }

    static string FormatDownloadSize(long bytes)
    {
        if (bytes <= 0)
            return Loc.Get("disk_small");
        var gb = bytes / 1_000_000_000.0;
        if (gb >= 1)
            return Loc.Format("size_gb", $"{gb:0.#}");
        var mb = bytes / 1_000_000.0;
        if (mb >= 1)
            return Loc.Format("size_mb", $"{mb:0}");
        return Loc.Get("disk_small");
    }

    void OnBrowseSimpleInstall(object sender, RoutedEventArgs e)
    {
        var dir = PickInstallFolder();
        if (dir is null)
            return;
        ApplyNewInstallFolder(dir);
    }

    void OnSimpleInstallRootLostFocus(object sender, RoutedEventArgs e)
    {
        var root = TxtSimpleInstallRoot?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(root) ||
            string.Equals(root, _settings.InstallPath, StringComparison.OrdinalIgnoreCase))
        {
            RefreshSimpleInstallCopy();
            return;
        }

        if (!TryAcceptInstallPath(root))
        {
            SyncInstallPathBoxes(_settings.InstallPath);
            return;
        }

        _ = AdoptInstallFolderAsync(root, persist: true);
    }

    static bool GameConfirmed(string root, InstallHealthReport health)
    {
        if (NeedsSetup(root))
            return false;
        if (!health.Enforced)
            return true;
        return health.IsReady || health.NeedsUpdate;
    }

    async Task AdoptInstallFolderAsync(string dir, bool persist)
    {
        if (_verifyBusy || _installBusy)
            return;
        if (string.IsNullOrWhiteSpace(dir) || !TryAcceptInstallPath(dir))
            return;

        SyncInstallPathBoxes(dir);
        if (persist)
            PersistSettingsFromUi();
        else
            _settings.InstallPath = dir;

        try
        {
            if (_manifest is null)
                await LoadChannelAsync().ConfigureAwait(true);

            var quick = ContentInstallService.Assess(_manifest, dir, true, true);
            if (!quick.Enforced && GameConfirmed(dir, quick))
            {
                ReloadPlaylistsAndMaps(selectSaved: true);
                InvalidateHealthMemo();
                RefreshInstallStateLabels();
                SetSimpleStatus(Loc.Get("ready_to_play"));
                Log("Folder check ok: " + quick.Summary);
                return;
            }

            _verifyBusy = true;
            ShowSimpleSetupLayout(true);
            ShowSimpleInstallBar(true);
            SetSimpleStatus(Loc.Get("status_checking"));
            RefreshSimplePlayButton();
            Log("Checking install folder: " + dir);

            var health = await ContentInstallService.VerifyExistingAsync(
                _manifest, dir, CreateVerifyProgress()).ConfigureAwait(true);

            ReloadPlaylistsAndMaps(selectSaved: true);
            RefreshInstallStateLabels(known: null);
            RefreshSimpleInstallCopy();

            if (GameConfirmed(dir, health))
            {
                var updateForced = health.NeedsUpdate &&
                    LaneBlocksPlay(health, CachedDownloadGate(), requireClient: true, requireServer: true);
                SetSimpleStatus(updateForced
                    ? Loc.Get("status_update_available")
                    : health.NeedsUpdate
                        ? Loc.Get("status_play_downloads_off")
                        : Loc.Get("ready_to_play"));
                Log("Folder check ok: " + health.Summary);
            }
            else if (NeedsSetup(dir))
            {
                SetSimpleStatus(Loc.Get("status_no_game_folder"));
                Log("Folder check: no game — " + health.Summary);
            }
            else
            {
                SetSimpleStatus(Loc.Get("status_files_need_repair"));
                Log("Folder check blocked: " + health.Summary);
            }

            ApplyInstallDiskStatus();
        }
        catch (Exception ex)
        {
            SetSimpleStatus(Loc.Get("status_check_failed"));
            Log("Folder check failed: " + ex.Message);
        }
        finally
        {
            _verifyBusy = false;
            ShowSimpleInstallBar(false);
            InvalidateHealthMemo();
            RefreshInstallStateLabels();
        }
    }

    Progress<ContentInstallProgress> CreateVerifyProgress() =>
        new(p => ApplyInstallProgress(p, logEvery: false));

    public sealed class TransferRow
    {
        public string Name { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public double Percent { get; init; }
    }

    void PaintActiveTransfers(IReadOnlyList<ActiveTransfer>? active)
    {
        if (ListActiveTransfers is null)
            return;
        if (active is null || active.Count == 0)
        {
            ListActiveTransfers.Visibility = Visibility.Collapsed;
            ListActiveTransfers.ItemsSource = null;
            return;
        }

        var rows = new List<TransferRow>(active.Count);
        foreach (var t in active)
        {
            var name = t.Path;
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < name.Length)
                name = name[(slash + 1)..];
            rows.Add(new TransferRow
            {
                Name = name,
                Detail = t.Total > 0
                    ? FormatBytes(t.Done) + " / " + FormatBytes(t.Total)
                    : FormatBytes(t.Done),
                Percent = t.Percent,
            });
        }

        ListActiveTransfers.ItemsSource = rows;
        ListActiveTransfers.Visibility = Visibility.Visible;
    }

    void ApplyInstallProgress(ContentInstallProgress p, bool logEvery)
    {
        var now = DateTime.UtcNow;
        var file = !string.IsNullOrWhiteSpace(p.FileName)
            ? p.FileName!
            : (p.Message ?? string.Empty);
        var bytes = p.Unit switch
        {
            ProgressUnit.Bytes => true,
            ProgressUnit.Items => false,
            _ => LooksLikeBytes(p.Total),
        };

        // A scan counts files in Current and the bytes it has read in JobCurrent,
        // so the rate comes off a different counter than the bar does.
        var scanning = p.Unit == ProgressUnit.Items && p.JobTotal > 0;
        var rateBytes = scanning ? p.JobCurrent : p.Current;
        var hasRate = bytes || scanning;

        var pct = scanning
            ? Math.Min(100.0, 100.0 * p.JobCurrent / p.JobTotal)
            : p.Total > 0
                ? Math.Min(100.0, 100.0 * p.Current / p.Total)
                : 0;

        // Keyed on the phase, not the file: several objects transfer at once and
        // the reported name changes every tick, which would restart the rate
        // window before it ever produced a number.
        var markKey = p.Unit == ProgressUnit.Unknown
            ? file
            : (p.Phase ?? string.Empty) + "|" + p.Track;

        if (!string.Equals(markKey, _progressMarkFile, StringComparison.Ordinal))
        {
            _progressMarkFile = markKey;
            _progressMarkBytes = rateBytes;
            _progressMarkUtc = now;
            _progressRate = 0;
        }
        else if (rateBytes < _progressMarkBytes)
        {
            _progressMarkBytes = rateBytes;
            _progressMarkUtc = now;
        }
        else if (hasRate && (now - _progressMarkUtc).TotalSeconds >= 0.4)
        {
            var dt = (now - _progressMarkUtc).TotalSeconds;
            var inst = (rateBytes - _progressMarkBytes) / dt;
            _progressRate = _progressRate <= 0 ? inst : (_progressRate * 0.65) + (inst * 0.35);
            _progressMarkBytes = rateBytes;
            _progressMarkUtc = now;
        }

        var phaseLabel = PhaseLabel(p.Phase);
        var step = p.StepCount > 0
            ? Loc.Format("part_of", Math.Max(1, p.StepIndex), p.StepCount)
            : "";
        if (!string.IsNullOrWhiteSpace(p.Track))
            step = string.IsNullOrEmpty(step) ? p.Track : step + "  ·  " + p.Track;

        string bytesLine = "";
        string rateLine = "";
        if (p.Unit == ProgressUnit.Items)
        {
            if (p.ItemsTotal > 0)
                bytesLine = Loc.Format("files_progress", p.ItemsDone, p.ItemsTotal);
            if (p.JobTotal > 0)
            {
                rateLine = Loc.Format(
                    "checked_progress", FormatBytes(p.JobCurrent), FormatBytes(p.JobTotal));
                if (_progressRate > 1)
                {
                    rateLine += "  \u00b7  " + FormatRate(_progressRate);
                    var left = p.JobTotal - p.JobCurrent;
                    if (left > 0)
                    {
                        rateLine += "  \u00b7  " + Loc.Format(
                            "eta_remaining", FormatEta(left / _progressRate));
                    }
                }
            }
        }
        else if (bytes && p.Total > 0)
        {
            bytesLine = FormatBytes(p.Current) + " / " + FormatBytes(p.Total);
            if (_progressRate > 1)
            {
                rateLine = FormatRate(_progressRate);
                var remain = p.JobTotal > p.JobCurrent
                    ? p.JobTotal - p.JobCurrent
                    : (p.Total > p.Current ? p.Total - p.Current : 0);
                if (remain > 0)
                    rateLine += "  ·  " + Loc.Format("eta_remaining", FormatEta(remain / _progressRate));
            }
        }
        else if (p.Total == 100)
        {
            bytesLine = $"{pct:0}%";
        }
        else if (p.Total > 0)
        {
            bytesLine = p.Current + " / " + p.Total;
        }

        var overallPct = p.StepCount > 0
            ? Math.Min(100.0, 100.0 * Math.Max(0, p.StepIndex - 1) / p.StepCount
                              + (pct / 100.0) * (100.0 / p.StepCount))
            : pct;
        var status = phaseLabel;
        if (!string.IsNullOrWhiteSpace(file))
            status += "  ·  " + file;
        if (!string.IsNullOrWhiteSpace(bytesLine))
            status += "  ·  " + bytesLine;
        if (!string.IsNullOrWhiteSpace(rateLine))
            status += "  ·  " + rateLine;

        var uiDue = (now - _progressLastUiUtc).TotalMilliseconds >= 80
                    || p.Current >= p.Total
                    || string.Equals(p.Phase, "done", StringComparison.OrdinalIgnoreCase);
        if (!uiDue)
            return;
        _progressLastUiUtc = now;

        if (BarInstall is not null)
            BarInstall.Value = overallPct;
        if (BarSimpleInstall is not null)
        {
            // The setup card already carries a bar; two would track in lockstep.
            var setupCard = PanelSimpleSetup?.Visibility == Visibility.Visible;
            BarSimpleInstall.Value = overallPct;
            BarSimpleInstall.Visibility = setupCard ? Visibility.Collapsed : Visibility.Visible;
        }

        var jobBytes = bytes && p.JobTotal > 0
            ? FormatBytes(p.JobCurrent) + " / " + FormatBytes(p.JobTotal)
            : bytesLine;
        if (p.Unit == ProgressUnit.Items)
            jobBytes = bytesLine;

        if (PanelSimpleInstallProgress is not null)
            PanelSimpleInstallProgress.Visibility = Visibility.Visible;
        if (TxtSimpleProgressPhase is not null)
            TxtSimpleProgressPhase.Text = phaseLabel;
        if (TxtSimpleProgressFile is not null)
        {
            TxtSimpleProgressFile.Text = p.Unit != ProgressUnit.Items && p.ItemsTotal > 0
                ? Loc.Format("files_progress", p.ItemsDone, p.ItemsTotal)
                : file;
        }
        if (BarSimpleFile is not null)
            BarSimpleFile.Value = overallPct;
        PaintActiveTransfers(p.Active);
        if (TxtSimpleProgressBytes is not null)
            TxtSimpleProgressBytes.Text = jobBytes;
        if (TxtSimpleProgressStep is not null)
            TxtSimpleProgressStep.Text = step;
        if (TxtSimpleProgressRate is not null)
            TxtSimpleProgressRate.Text = rateLine;

        SetSimpleStatus(status);
        if (TxtInstallStatus is not null)
            TxtInstallStatus.Text = status;

        if (logEvery || p.Current == 0 || p.Current >= p.Total ||
            string.Equals(p.Phase, "done", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Phase, "manifest", StringComparison.OrdinalIgnoreCase))
        {
            Log(status);
        }
    }

    static bool LooksLikeBytes(long total) => total >= 1024;

    static string PhaseLabel(string? phase) => (phase ?? "").Trim().ToLowerInvariant() switch
    {
        "download" or "copy" or "http" or "reget" or "fetch" => Loc.Get("phase_download"),
        "scan" => Loc.Get("phase_check"),
        "unpack" => Loc.Get("phase_unpack"),
        "manifest" => Loc.Get("phase_manifest"),
        "recheck" or "verify" or "check" or "repair" or "sweep" => Loc.Get("phase_check"),
        "done" => Loc.Get("phase_done"),
        "" => Loc.Get("working"),
        _ => char.ToUpperInvariant(phase![0]) + phase[1..],
    };

    /// <summary>
    /// Decimal units. The published download size, the CDN figures and the
    /// manifest are all decimal; showing GiB under a "GB" label made the same
    /// install read 37.7 here and 40.5 everywhere else.
    /// </summary>
    static string FormatBytes(long n)
    {
        if (n < 1000)
            return Loc.Format("size_b", n);
        double v = n;
        string[] keys = ["size_kb", "size_mb", "size_gb", "size_tb"];
        var u = -1;
        do
        {
            v /= 1000.0;
            u++;
        } while (v >= 1000.0 && u < keys.Length - 1);
        var num = v >= 10 ? $"{v:0}" : $"{v:0.00}";
        return Loc.Format(keys[u], num);
    }

    static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1000)
            return Loc.Format("rate_bs", Loc.Format("size_b", $"{bytesPerSec:0}"));
        return Loc.Format("rate_bs", FormatBytes((long)bytesPerSec));
    }

    static string FormatEta(double seconds)
    {
        if (seconds < 60)
            return Loc.Format("eta_s", $"{seconds:0}");
        if (seconds < 3600)
            return Loc.Format("eta_ms", (int)(seconds / 60), $"{seconds % 60:0}");
        return Loc.Format("eta_hm", (int)(seconds / 3600), (int)(seconds / 60) % 60);
    }

    private void ShowSimpleInstallBar(bool visible)
    {
        if (BarSimpleInstall is not null)
        {
            var setup = PanelSimpleSetup?.Visibility == Visibility.Visible;
            BarSimpleInstall.Visibility =
                visible && !setup ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                BarSimpleInstall.Value = 0;
        }
        PaintInstallControls();

        if (PanelSimpleInstallProgress is not null)
        {
            PanelSimpleInstallProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                if (BarSimpleFile is not null)
                    BarSimpleFile.Value = 0;
                if (TxtSimpleProgressPhase is not null)
                    TxtSimpleProgressPhase.Text = Loc.Get("working_ellipsis");
                if (TxtSimpleProgressFile is not null)
                    TxtSimpleProgressFile.Text = "";
                if (ListActiveTransfers is not null)
                {
                    ListActiveTransfers.ItemsSource = null;
                    ListActiveTransfers.Visibility = Visibility.Collapsed;
                }
                if (TxtSimpleProgressBytes is not null)
                    TxtSimpleProgressBytes.Text = "";
                if (TxtSimpleProgressStep is not null)
                    TxtSimpleProgressStep.Text = "";
                if (TxtSimpleProgressRate is not null)
                    TxtSimpleProgressRate.Text = "";
            }
        }

        if (visible)
        {
            _progressMarkUtc = DateTime.UtcNow;
            _progressMarkBytes = 0;
            _progressMarkFile = "";
            _progressRate = 0;
            _progressLastUiUtc = DateTime.MinValue;
        }
    }

    bool TryHostPassword(out string password, out string? refuseReason)
    {
        password = ReadPasswordBox();
        refuseReason = null;
        if (!IsPasswordProtectOn() && string.IsNullOrEmpty(password))
            return true;
        if (LaunchArgs.IsSafeServerPassword(password))
            return true;
        refuseReason =
            "server password refused: illegal characters (no quotes, semicolons, or backslashes)";
        return false;
    }

    bool TryAcceptInstallPath(string path, bool quiet = false)
    {
        if (InstallPathPolicy.IsForbidden(path, AppContext.BaseDirectory))
        {
            RejectInstallPath(quiet,
                "Pick a folder that is not AppData or the launcher's own folder.");
            return false;
        }

        // Probe only on explicit accepts; the quiet path runs per keystroke.
        if (!quiet && !InstallPathPolicy.TryCreateWritable(path))
        {
            RejectInstallPath(quiet,
                "Windows blocks writing to that folder without admin rights. Pick another folder.");
            return false;
        }

        return true;
    }

    void RejectInstallPath(bool quiet, string msg)
    {
        if (quiet)
            return;
        if (_settings.SimpleMode)
            SetSimpleStatus(msg);
        else
            MessageBox.Show(this, msg, Loc.Get("title_install_path"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    bool ConfirmAndGateInstall(InstallMode mode, string installPath)
    {
        if (_manifest is null)
            return false;

        try
        {
            if (TryMeasureInstallDisk(mode, installPath, out var planned, out var need, out var free) &&
                free >= 0 && free < need)
            {
                var msg = FormatDiskShortage(need, planned, free);
                SetSimpleStatus(msg);
                MessageBox.Show(this, msg, Loc.Get("title_disk"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!_settings.InitialInstallAccepted && planned > 512L * 1024 * 1024)
            {
                if (_settings.SimpleMode && NeedsSetup(installPath))
                {
                    _settings.InitialInstallAccepted = true;
                    _settings.AutoApplyUpdates = true;
                    SettingsStore.Save(_settings);
                }
                else
                {
                    var ask = Loc.Format(
                        "confirm_download",
                        FormatDownloadSize(planned),
                        FormatDownloadSize(need));
                    var r = MessageBox.Show(this, ask, Loc.Get("title_install_game"),
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes)
                        return false;
                    _settings.InitialInstallAccepted = true;
                    _settings.AutoApplyUpdates = true;
                    SettingsStore.Save(_settings);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Install gate: " + ex.Message);
        }

        return true;
    }

    async Task MaybeAutoApplySmallUpdateAsync()
    {
        if (!_settings.AutoApplyUpdates || _manifest is null || _installBusy)
            return;
        var gate = CachedDownloadGate();
        if (!gate.AnyLane)
            return;
        var root = TxtInstallRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
            return;
        if (ProcessSpawner.IsRoleAlive(LaunchRole.Client, root) ||
            ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root))
            return;

        try
        {
            var health = await Task.Run(
                () => ContentInstallService.Assess(_manifest, root, true, true));
            if (!health.NeedsUpdate)
                return;
            if (!LaneBlocksPlay(health, gate, requireClient: true, requireServer: true))
                return;
            var plan = InstallPlanner.Build(_manifest, InstallMode.Full, root);
            InstallPlanner.FilterDownloadLanes(plan, gate.Content, gate.Platform);
            InstallPlanner.ExcludeCurrentTracks(plan, _manifest, root);
            InstallPlanner.MeasureWork(plan, _manifest, out var planned, out _);
            if (planned <= 0 || planned > 512L * 1024 * 1024)
                return;
            Log($"Auto-applying small update ({planned} bytes).");
            _ = RunInstallAsync(InstallMode.Full);
        }
        catch (Exception ex)
        {
            Log("Auto-update skipped: " + ex.Message);
        }
    }

    async void OnRemoveGameFiles(object sender, RoutedEventArgs e)
    {
        var root = TxtInstallRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBox.Show(this, Loc.Get("msg_no_folder_remove"), Loc.Get("title_remove"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ProcessSpawner.IsRoleAlive(LaunchRole.Client, root) ||
            ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, root))
        {
            MessageBox.Show(this, Loc.Get("msg_stop_first"), Loc.Get("title_remove"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryAcceptInstallPath(root))
            return;

        var confirmed = ConfirmPhraseWindow.Ask(
            this,
            Loc.Get("remove_headline"),
            Loc.Get("remove_detail"),
            root,
            Loc.Get("remove_phrase"));
        if (!confirmed)
        {
            Log("Remove game cancelled.");
            return;
        }

        // The shell's own log used to live under the install and kept a handle
        // open, which failed the whole delete on the first file.
        LauncherLog.Close();
        SetSimpleStatus(Loc.Get("status_removing"));
        SevenZipLocator.KillOurUnpackers();

        RemoveReport report;
        try
        {
            report = await Task.Run(() => DirectoryRemover.Remove(root)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Format("msg_delete_failed", ex.Message), Loc.Get("title_remove"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log($"Remove game: deleted {report.FilesDeleted} file(s), " +
            $"{report.BytesDeleted / (1024 * 1024)} MB, {report.Failures.Count} failure(s).");

        _settings.InitialInstallAccepted = false;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log("Settings save failed: " + ex.Message); }

        InvalidateHealthMemo();
        RefreshInstallStateLabels();

        if (report.RootRemoved && report.Failures.Count == 0)
        {
            SetSimpleStatus(Loc.Get("status_removed"));
            return;
        }

        var stuck = string.Join(Environment.NewLine,
            report.Failures.Take(8).Select(f => f.Path));
        if (report.Failures.Count > 8)
            stuck += Environment.NewLine + "...";

        SetSimpleStatus(Loc.Get("status_removed_partial"));
        MessageBox.Show(
            this,
            Loc.Format("msg_remove_partial", report.FilesDeleted, report.Failures.Count) +
                Environment.NewLine + Environment.NewLine + stuck,
            Loc.Get("title_remove"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
