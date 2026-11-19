using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>
/// The game build is a property of the install folder (see <see cref="GameBuilds"/>). The selector only moves
/// between the last folder used for each build; the theme follows whichever folder is active.
/// </summary>
public partial class MainWindow
{
    GameBuild? _syncedBuild;
    string? _syncedPath;
    bool _paintingBuild;
    bool _buildChannelLoading = true;

    /// <summary>A folder with a game says what it is; an empty one will install the season picked for it.</summary>
    GameBuild BuildOf(string? path)
    {
        if (GameBuilds.IsS30(path))
            return GameBuild.S30;
        return NeedsSetup(path ?? string.Empty) ? _settings.SetupBuild : GameBuild.S21;
    }

    void SyncGameBuild(string path)
    {
        var build = BuildOf(path);
        MapOption.UseS30Titles = build == GameBuild.S30;
        if (IsBuildFolder(path, build))
        {
            if (build == GameBuild.S30)
                _settings.S30InstallPath = path;
            else
                _settings.S21InstallPath = path;
        }

        // Runs on every settings pass; the theme, taskbar and folder probes only matter when something moved.
        var trimmed = path.Trim();
        if (_syncedBuild == build && string.Equals(_syncedPath, trimmed, StringComparison.OrdinalIgnoreCase))
            return;
        var buildChanged = _syncedBuild is not null && _syncedBuild != build;
        _syncedBuild = build;
        _syncedPath = trimmed;

        if (buildChanged)
            FlashBuildChange();
        ThemePalette.Apply(build);
        TaskbarIdentity.Apply(new WindowInteropHelper(this).Handle, build);
        SyncShortcutCheckbox();

        PaintBuildUi(build);
        if (!IsLoaded)
            return;
        RefreshArgPreviews();
        if (buildChanged)
            _ = SwitchBuildChannelAsync(trimmed);
    }

    /// <summary>The old season's sizes and Install button must not outlive the switch, so setup repaints on both ends.</summary>
    async Task SwitchBuildChannelAsync(string root)
    {
        _manifest = null;
        _buildChannelLoading = true;
        ClearServerList();
        RefreshAfterBuildChannel();
        var catalog = ReloadCatalogForBuildAsync();
        await SwitchRingAsync(root).ConfigureAwait(true);
        _buildChannelLoading = false;
        RefreshAfterBuildChannel();
        await RefreshServerListAsync(quiet: true).ConfigureAwait(true);
        await catalog.ConfigureAwait(true);
    }

    void RefreshAfterBuildChannel()
    {
        RefreshSimpleInstallCopy();
        InvalidateHealthMemo();
        RefreshSimplePlayButton();
        PaintInstallControls();
        if (PanelSimpleSetup?.Visibility == Visibility.Visible && _simplePlayKind == SimplePlayKind.Install)
        {
            SetSimpleStatus(_installDiskBlocked && _installDiskMessage.Length > 0
                ? _installDiskMessage
                : Loc.Get("status_pick_folder_install"));
        }
    }

    void PaintBuildUi(GameBuild active)
    {
        string Tag(GameBuild build)
        {
            if (build == active)
                return Loc.Get("build_active");
            var kept = build == GameBuild.S30 ? _settings.S30InstallPath : _settings.S21InstallPath;
            return Loc.Get(IsBuildFolder(kept, build) && !NeedsSetup(kept) ? "build_installed" : "build_not_installed");
        }

        var s21 = Tag(GameBuild.S21);
        var s30 = Tag(GameBuild.S30);
        TxtCardS21Tag.Text = TxtSetupS21Tag.Text = s21;
        TxtCardS30Tag.Text = TxtSetupS30Tag.Text = s30;
        _paintingBuild = true;
        foreach (var radio in new[] { RbSetS21, RbSetupS21 })
            radio.IsChecked = active == GameBuild.S21;
        foreach (var radio in new[] { RbSetS30, RbSetupS30 })
            radio.IsChecked = active == GameBuild.S30;
        _paintingBuild = false;

        foreach (var (button, build) in new[] { (BtnServersS21, GameBuild.S21), (BtnServersS30, GameBuild.S30),
                     (BtnHdrS21, GameBuild.S21), (BtnHdrS30, GameBuild.S30) })
        {
            button.Background = build == active ? (Brush)FindResource("PrimaryFill") : Brushes.Transparent;
            button.Foreground = (Brush)FindResource(build == active ? "PrimaryText" : "TextMuted");
        }

        // S30.1 ships only the DirectX 12 image and serves no playtest ring.
        var s21Only = active == GameBuild.S30 ? Visibility.Collapsed : Visibility.Visible;
        RowUseDx12.Visibility = s21Only;
        RowClientDx12.Visibility = s21Only;
        RowJoinPlaytests.Visibility = s21Only;
        BdgHdrS30New.Visibility = active != GameBuild.S30 && s30 == Loc.Get("build_not_installed")
            ? Visibility.Visible : Visibility.Collapsed;

        // Season 21 servers run only at the stock rate.
        var s30Only = active == GameBuild.S30 ? Visibility.Visible : Visibility.Collapsed;
        RowPopTickRate.Visibility = s30Only;
        RowAdvTickRate.Visibility = s30Only;
        RefreshPlayPills();
        RefreshArgPreviews();
        RefreshReplaceS21Option();
    }

    /// <summary>The rate this launch uses; the saved pick only applies to R5F 2.0.</summary>
    int EffectiveTickRate => ActiveBuild == GameBuild.S30 ? _settings.TickRate : TickRates.Stock;

    void OnPickBuildS21(object sender, RoutedEventArgs e) => OnBuildPicked(GameBuild.S21);

    void OnPickBuildS30(object sender, RoutedEventArgs e) => OnBuildPicked(GameBuild.S30);

    void OnBuildPicked(GameBuild want)
    {
        if (IsLoaded && !_paintingBuild)
            SwitchGameBuild(want);
    }

    /// <summary>Moves to the season's folder, or to install it when it has none yet.</summary>
    void SwitchGameBuild(GameBuild want)
    {
        var current = ReadInstallPathBox();
        if (BuildOf(current) == want)
            return;
        if (SessionLive || _installBusy || _verifyBusy)
        {
            MessageBox.Show(this, Loc.Get("msg_stop_first"), Loc.Get("label_game_build"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PaintBuildUi(BuildOf(current));
            return;
        }

        var kept = want == GameBuild.S30 ? _settings.S30InstallPath : _settings.S21InstallPath;
        _settings.SetupBuild = want;
        Log($"Game build: {BuildLabel(want)}");
        if (IsBuildFolder(kept, want))
        {
            ApplyNewInstallFolder(kept);
        }
        else if (NeedsSetup(current) && !GameBuilds.IsS30(current))
        {
            // Nothing is in this folder yet, so it can stay and simply install the other season.
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
            SyncGameBuild(current);
        }
        else
        {
            ApplyNewInstallFolder(NewFolderFor(want, current));
        }
        SyncGameBuild(ReadInstallPathBox());
    }

    /// <summary>R5F 2.0 goes next to an existing Season 21 folder, so it lands on the drive the player already chose.</summary>
    static string NewFolderFor(GameBuild want, string current) =>
        want == GameBuild.S30 && IsBuildFolder(current, GameBuild.S21)
            ? current.Trim().TrimEnd('\\', '/') + " S30"
            : DefaultFolderFor(want);

    static string DefaultFolderFor(GameBuild build) =>
        build == GameBuild.S30 ? SettingsStore.DefaultInstallPath.TrimEnd('\\', '/') + " S30" : SettingsStore.DefaultInstallPath;

    static bool IsBuildFolder(string? path, GameBuild build)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || GameBuilds.Detect(path) != build)
            return false;
        return build == GameBuild.S30 || File.Exists(Path.Combine(path, ProcessSpawner.ClientExeName));
    }

    static string BuildLabel(GameBuild build) =>
        Loc.Get(build == GameBuild.S30 ? "game_build_s30" : "game_build_s21");

    /// <summary>--build on a cold start: moves to the remembered folder before the window first paints.</summary>
    bool AdoptRequestedBuildAtStartup()
    {
        if (!GameBuildLink.TakePending(out var want))
            return false;
        if (BuildOf(_settings.InstallPath) == want)
            return true;

        var target = want == GameBuild.S30 ? _settings.S30InstallPath : _settings.S21InstallPath;
        if (IsBuildFolder(target, want))
        {
            _settings.InstallPath = target;
        }
        else
        {
            _settings.SetupBuild = want;
            if (!NeedsSetup(_settings.InstallPath) || GameBuilds.IsS30(_settings.InstallPath))
                _settings.InstallPath = DefaultFolderFor(want);
        }
        return true;
    }

    /// <summary>--build on a second start; the switch asks for a folder when none is remembered.</summary>
    internal void ConsumePendingBuild()
    {
        if (!GameBuildLink.TakePending(out var want))
            return;
        if (SessionLive)
        {
            Log($"Game build {BuildLabel(want)} requested while a game session is up; ignored.");
            return;
        }
        SwitchGameBuild(want);
    }

    /// <summary>
    /// S30.1 launch lines: the server skips the S21 Flowstate pak (an S30.1 install does not carry it); the
    /// client skips the intro video, when offline the EA app sign-in (-noorigin), and with HD textures off
    /// the optional texture streams (-hd_textures 0).
    /// </summary>
    IReadOnlyList<string> WithGameBuildArgs(LaunchRole role, string root, IReadOnlyList<string> args)
    {
        if (!GameBuilds.IsS30(root))
            return args;

        var list = args.ToList();
        void Add(params string[] tokens)
        {
            if (!list.Any(a => string.Equals(a, tokens[0], StringComparison.OrdinalIgnoreCase)))
                list.AddRange(tokens);
        }

        if (role == LaunchRole.Dedicated)
        {
            Add("+sdk_pak_load_flowstate", "0");
            return list;
        }

        Add("-novid");
        if (list.Any(a => string.Equals(a, "-offline", StringComparison.OrdinalIgnoreCase)))
            Add("-noorigin");
        if (!_settings.S30HdTextures || S30HdSetUnfinished(root))
            Add("-hd_textures", "0");
        return list;
    }

    // A launcher-installed folder streams HD only once the whole set is on disk; a
    // partly downloaded or partly removed set would leave paks naming missing streams.
    static bool S30HdSetUnfinished(string root)
    {
        var statePath = System.IO.Path.Combine(root, ProductConstants.InstallStateFileName);
        if (!System.IO.File.Exists(statePath))
            return false;
        try
        {
            var state = InstallStateIO.Load(statePath);
            return !(state.HdEnabled && state.HdReady);
        }
        catch
        {
            return true;
        }
    }
}
