using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private LinuxSettings _s = LinuxSettings.Load();

    /// <summary>Test-line version. Windows Velopack feed never sees this;
    /// numeric 2.0.0 in csproj, display string Linux-2.00.</summary>
    [ObservableProperty] private string _version = ResolveVersion();

    [ObservableProperty] private string _installPath = "";
    [ObservableProperty] private string _prefixPath = "";
    [ObservableProperty] private string _protonDir = "";
    [ObservableProperty] private ObservableCollection<ProtonEntry> _protons = new();
    [ObservableProperty] private ProtonEntry? _selectedProton;

    // The EA App runs under Proton in the game's prefix, so there is no runtime to
    // choose. EaStatus and the verdict lines below are the player-facing half of what
    // is left: whether EA is installed in the prefix, whether its channel answers, and
    // what the game itself said about its identity.
    [ObservableProperty] private bool _startEaWithGame = true;
    [ObservableProperty] private bool _autoDownloadProtonGe = true;
    [ObservableProperty] private string _eaChannelStatus = "";
    [ObservableProperty] private string _eaIdentityStatus = "";

    /// <summary>The online pre-flight's own line: the state of the EA channel before
    /// a client that has to prove an account is started, and the identity verdict
    /// while one runs. Separate from <see cref="EaChannelStatus"/>, which is the
    /// read-only probe's answer to "is anything listening right now" — this one is
    /// "may I launch".</summary>
    [ObservableProperty] private string _eaBridgeStatus = "";

    /// <summary>The EA App this launcher started, when it started one. Kept so it
    /// is not collected while its output readers are still running.</summary>
    System.Diagnostics.Process? _eaProcess;

    /// <summary>The watcher over a channel that was up. Null means nothing is being
    /// watched — which is the normal state before an online launch.</summary>
    EaBridgeWatcher? _eaWatcher;

    [ObservableProperty] private string _eaStatus = "checking…";
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private string _log = "";

    // Download progress for the EA installer / Proton-GE transfers. Kept out of
    // the log on purpose: a 550 MB download would otherwise emit one line per
    // megabyte into the log and the console pane.
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private bool _downloading;
    [ObservableProperty] private double _downloadFraction;

    // First-run EA popup state (shown when EA App not detected in prefix).
    [ObservableProperty] private bool _showEaDialog;
    [ObservableProperty] private string _eaDialogText = "";
    [ObservableProperty] private bool _busy;

    // Play options (mirror Windows Simple panel)
    [ObservableProperty] private string _playlist = "survival_dev";
    [ObservableProperty] private string _map = "mp_rr_divided_moon_mu1";
    [ObservableProperty] private int _port = 37015;
    [ObservableProperty] private bool _offlineNoAuth;
    [ObservableProperty] private bool _hostOnline;
    [ObservableProperty] private bool _dev;
    [ObservableProperty] private bool _cheats = true;
    [ObservableProperty] private bool _useDx12;
    [ObservableProperty] private string _clientExtra = "";
    [ObservableProperty] private string _dediExtra = "";
    [ObservableProperty] private string _joinTarget = "";
    [ObservableProperty] private string _joinPassword = "";

    // Simple-shell extras (Windows parity)
    [ObservableProperty] private string _simpleStatus = "";
    [ObservableProperty] private string _dediPassword = "";
    [ObservableProperty] private bool _passwordProtect;
    [ObservableProperty] private int _clientWidth;
    [ObservableProperty] private int _clientHeight;
    /// <summary>Windowed / borderless / fullscreen, one of
    /// <see cref="DisplayModes.WindowModes"/>. Always one of the three: the settings
    /// file is clamped on load, so the launch line always carries the flag and the
    /// Res menu always has something ticked.</summary>
    [ObservableProperty] private string _clientWindowMode = DisplayModes.DefaultWindowMode;
    [ObservableProperty] private string _clientArgs = "";
    [ObservableProperty] private string _dediArgs = "";
    [ObservableProperty] private string _clientPreview = "";
    [ObservableProperty] private string _dediPreview = "";
    [ObservableProperty] private int _downloadLimit;
    [ObservableProperty] private bool _openConsoleOnLaunch;
    [ObservableProperty] private bool _showUnlistedMaps;
    [ObservableProperty] private bool _joinWithoutDev = true;
    [ObservableProperty] private bool _keepLocalFiles;
    [ObservableProperty] private string _heroTitle = "";
    [ObservableProperty] private string _heroSubtitle = "";
    [ObservableProperty] private string _heroCommand = "";
    [ObservableProperty] private Avalonia.Media.IImage? _heroArt;
    [ObservableProperty] private bool _isSimpleMode = true;

    // Tab data
    [ObservableProperty] private ObservableCollection<ModeGroupVM> _modes = new();
    [ObservableProperty] private ObservableCollection<MapTileVM> _mapTiles = new();
    [ObservableProperty] private ObservableCollection<ServerRowVM> _servers = new();
    [ObservableProperty] private ObservableCollection<ModRowVM> _modsInstalled = new();
    [ObservableProperty] private ObservableCollection<ModRowVM> _modsBrowse = new();
    [ObservableProperty] private ObservableCollection<PatchNoteEntryVM> _notesEntries = new();
    [ObservableProperty] private ObservableCollection<CreditEntryVM> _creditsRows = new();
    [ObservableProperty] private ObservableCollection<LeaderboardRowVM> _leaderboardRows = new();
    [ObservableProperty] private ObservableCollection<LanguageRow> _languages = new();
    [ObservableProperty] private LanguageRow? _selectedLanguage;

    // Status lines for the networked tabs (master server features land after
    // the core launcher stabilizes on Linux).
    [ObservableProperty] private string _lbStatus = "";
    [ObservableProperty] private string _blogStatus = "";
    [ObservableProperty] private string _modsStatus = "";
    [ObservableProperty] private string _browserStatus = "";
    [ObservableProperty] private string _healthText = "—";
    [ObservableProperty] private string _txtHdTextures = "—";
    [ObservableProperty] private string _installStatus = "";

    public static string WebsiteUrl => "https://r5flowstate.org";
    /// <summary>The standalone server package the master server publishes. Same
    /// constant the Windows launcher opens (ProductConstants.DediPackageUrl); the
    /// link that uses it is shown only when the dedi lane is open.</summary>
    public static string DediPackageUrl => R5Flowstate.Contracts.ProductConstants.DediPackageUrl;
    public static string DiscordUrl => "https://discord.gg/r5reloaded";
    public static string GitHubUrl => "https://github.com/R5Flowstate/launcher";
    public static string PatreonUrl => "https://www.patreon.com/c/r5_CafeFPS";
    public static string KofiUrl => "https://ko-fi.com/r5r_colombia";
    public static string ToolsUrl => "https://github.com/CafeFPS";

    /// <summary>Dedi playlist / map ids for the advanced card combos.</summary>
    public ObservableCollection<string> PlaylistIds { get; } = new() { "survival_dev", "survival", "lobby", "1v1" };
    public ObservableCollection<string> MapIds { get; } = new();

    /// <summary>
    /// The install's own playlists and maps, parsed from its playlist def and
    /// localization files. Windows holds the same thing on the window
    /// (<c>_catalog</c>, built by <c>ReloadPlaylistsAndMaps</c>); this port keeps
    /// it on the view model because the combos, the labels and the console's
    /// steering all read it from here. It starts empty — which is what Windows
    /// shows before its catalog loads, not a Linux-only degradation: an empty
    /// catalog means an empty map list and a prettified stem, nothing invented.
    /// </summary>
    PlaylistCatalog _catalog = new();

    internal PlaylistCatalog Catalog => _catalog;

    /// <summary>The install's stem → friendly-name table, for every label that
    /// names a map (browser rows, leaderboard splits, match details).</summary>
    public IReadOnlyDictionary<string, string> CatalogMapNames => _catalog.MapNames;

    private static string ResolveVersion()
    {
        try
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                if (plus > 0) info = info[..plus];
                return info.Trim();
            }
        }
        catch { }
        return "Linux-2.00";
    }

    public MainViewModel()
    {
        InstallPath = string.IsNullOrWhiteSpace(_s.InstallPath) ? LinuxSettings.DefaultInstallPath() : _s.InstallPath;
        PrefixPath = string.IsNullOrWhiteSpace(_s.PrefixPath) ? LinuxSettings.DefaultPrefixPath : _s.PrefixPath;
        ProtonDir = _s.ProtonDir;
        StartEaWithGame = _s.StartEaWithGame;
        AutoDownloadProtonGe = _s.AutoDownloadProtonGe;
        Playlist = _s.DediPlaylist; Map = _s.DediMap; Port = _s.DediPort;
        OfflineNoAuth = _s.OfflineNoAuth; HostOnline = _s.DediHostOnline;
        Dev = _s.DevProfile; Cheats = _s.Cheats; UseDx12 = _s.UseDx12;
        ClientExtra = _s.ClientLaunchArguments; DediExtra = _s.DediLaunchArguments;
        DediPassword = _s.DediPassword; PasswordProtect = _s.DediPasswordEnabled;
        ClientWidth = _s.ClientWidth; ClientHeight = _s.ClientHeight;
        ClientWindowMode = DisplayModes.ClampWindowMode(_s.ClientWindowMode);
        ClientArgs = _s.ClientLaunchArguments; DediArgs = _s.DediLaunchArguments;
        DownloadLimit = _s.DownloadLimitMbps;
        JoinWithoutDev = _s.JoinWithoutDev;
        OpenConsoleOnLaunch = _s.OpenConsoleOnLaunch;
        IsSimpleMode = _s.SimpleMode;
        SimpleStatus = Loc.Get("ready_to_play");
        // The leaderboard line starts empty and is filled by the first refresh;
        // InitLeaderboards paints the column captions before any data exists.
        LbStatus = "";
        // The blog's own line starts empty; InitBlog restores the unread dot
        // from the stamp and the first refresh fills the list.
        BlogStatus = "";
        ModsStatus = Loc.Get("servers_refresh_hint");
        // The browser's own line starts empty and is filled by the first refresh;
        // InitBrowser restores last session's stars before any row exists.
        BrowserStatus = "";
        InitBrowser();
        InitLeaderboards();
        InitBlog();
        RebuildModeCards();
        BuildCredits();
        BuildNotes();
        BuildLanguages();
        RefreshProtons();
        CheckEaFirstRun();
        UpdatePreviews();
        // A line of context before anything goes wrong: the log outlives the
        // window, and a run that failed an hour ago reads very differently when
        // it is followed by the paths it was using. LogOnly, so the banner does
        // not become the first thing in the status bar.
        LogOnly($"R5Flowstate {Version} started  |  install={InstallPath}  |  prefix={PrefixPath}");
        // A settings file from the Wine era is migrated on load; say so once, here,
        // where there is a log to say it in.
        if (_s.MigrationNotice is { } notice)
            LogOnly(notice);
        LogOnly($"EA App: Proton, in the game's prefix ({PrefixPath})");
    }

    // ------------------------------------------------------------------ catalogs

    // ------------------------------------------------------------ mode cards
    //
    // Port of MainWindow.RebuildModeCards / SelectModeCard / KeepMapIfInPlaylist
    // / SortModeCardsByGroup (Windows MainWindow.xaml.cs:4149-4240, 5274), and of
    // RefreshHeroChrome (MainWindow.Loadscreen.cs:91). The rail, the hero and the
    // map grid all read the card list, so they cannot disagree about what is
    // selected -- which is exactly what one owner buys.

    /// <summary>
    /// Every family the playlist def declares, plus the synthesized Lobby card:
    /// Windows' _modeCards. The rail renders a grouped view of it (Avalonia has
    /// no CollectionViewSource grouping, so the runs are built here into
    /// ModeGroupVM), and the console's pickers and the steering will read this
    /// same list.
    /// </summary>
    internal ObservableCollection<ModeCardViewModel> ModeCards { get; } = new();

    /// <summary>Windows' _selectedMode.</summary>
    [ObservableProperty] private ModeCardViewModel? _selectedMode;

    internal void RebuildModeCards()
    {
        var keepId = SelectedMode?.Id ?? _s.LastModePlaylist;
        ModeCards.Clear();
        SelectedMode = null;

        var families = Catalog.Families;
        if (families.Count == 0)
            families = PlaceholderFamilies(Map);

        foreach (var family in families)
        {
            var card = ModeCardViewModel.FromFamily(family, Catalog.MapNames, _s.RememberedMap(family.Id));
            if (card.Maps.Count == 0)
                continue;
            ModeCards.Add(card);
        }

        // Windows adds a Lobby card when the install has none of its own: the
        // lobby is a real map with a playlist, but it is not a mode anyone plays
        // on nine maps, so it is pinned to one.
        if (!ModeCards.Any(c =>
                ModeCardViewModel.IsLobbyPlaylist(c.Id) ||
                (c.MapIsPinned && IsLobbyStem(c.SelectedMapStem))))
        {
            var lobbyMap = new MapOption(
                ModeCardViewModel.LobbyMapStem,
                MapLabels.PlayerName(ModeCardViewModel.LobbyMapStem, Catalog.MapNames),
                ModeCardViewModel.LobbyLaunchPlaylist);
            ModeCards.Add(new ModeCardViewModel(
                ModeCardViewModel.LobbyPlaylistId,
                ModeGroups.Apex,
                ModeCardViewModel.LocalizedTitle(ModeCardViewModel.LobbyPlaylistId, "Lobby"),
                ModeCardViewModel.LocalizedBlurb(ModeCardViewModel.LobbyPlaylistId, "S21 offline lobby."),
                new[] { lobbyMap },
                lobbyMap,
                mapIsPinned: true,
                fallbackPlaylistId: ModeCardViewModel.LobbyLaunchPlaylist));
        }

        SortModeCardsByGroup();
        RebuildModeRail();

        if (ModeCards.Count == 0)
        {
            // Nothing to offer: Windows sets status_no_modes here and lets the
            // installer fill the rail in. Never reached with the placeholder
            // above, and kept as the honest end state if it ever is.
            SelectedMode = null;
            MapTiles.Clear();
            RefreshHero();
            return;
        }

        ModeCardViewModel? pick = null;
        if (!string.IsNullOrWhiteSpace(keepId))
        {
            pick = ModeCards.FirstOrDefault(c =>
                       string.Equals(c.Id, keepId, StringComparison.OrdinalIgnoreCase))
                   ?? ModeCards.FirstOrDefault(c =>
                       string.Equals(c.PlaylistId, keepId, StringComparison.OrdinalIgnoreCase));
        }
        pick ??= ModeCards.FirstOrDefault(c => ModeCardViewModel.IsLobbyPlaylist(c.Id))
                 ?? ModeCards[0];
        SelectModeCard(pick, persist: false);
    }

    /// <summary>Windows keeps the rail's runs implicit in its CollectionViewSource;
    /// here they are explicit, with the group's own localized header.</summary>
    void RebuildModeRail()
    {
        Modes.Clear();
        foreach (var group in ModeGroups.Order)
        {
            var cards = ModeCards
                .Where(c => string.Equals(c.Group, group, StringComparison.Ordinal))
                .ToList();
            if (cards.Count == 0)
                continue;
            var vm = new ModeGroupVM(cards[0].GroupTitle);
            foreach (var card in cards)
                vm.Items.Add(new ModeItemVM(card));
            Modes.Add(vm);
        }
    }

    /// <summary>
    /// Port of MainWindow.SelectModeCard. <paramref name="persist"/> mirrors
    /// upstream: a rebuild selects without writing settings, a click writes them.
    /// Windows also queues a loadscreen and re-binds its map picker here; both of
    /// those now happen in <see cref="RefreshHero"/> (the chrome and the hero art),
    /// which every selection path already ends with, and the map grid is rebuilt
    /// from the card instead of from a picker's visibility.
    /// </summary>
    internal void SelectModeCard(ModeCardViewModel card, bool persist)
    {
        var previousStem = SelectedMode?.SelectedMapStem;
        foreach (var c in ModeCards)
            c.IsSelected = ReferenceEquals(c, card);
        SelectedMode = card;
        _s.LastModePlaylist = card.Id;
        KeepMapIfInPlaylist(card, previousStem);
        FillMapTiles(card);
        RefreshHero();
        if (persist)
            PersistModeSettings();
    }

    /// <summary>Pinned modes (Lobby, Firing Range) keep their own map.</summary>
    static void KeepMapIfInPlaylist(ModeCardViewModel card, string? previousStem)
    {
        if (card.MapIsPinned || string.IsNullOrWhiteSpace(previousStem))
            return;
        var match = card.Maps.FirstOrDefault(m =>
            string.Equals(m.Stem, previousStem, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            card.SelectedMap = match;
    }

    /// <summary>The rail groups on GroupTitle, so the list must already be in
    /// group order. Catalog order is group -> family order -> title, and LINQ
    /// sorts stably, so ranking the group alone keeps that and parks the
    /// synthetic Lobby card at the end of the Apex run where it was appended.</summary>
    void SortModeCardsByGroup()
    {
        var ordered = ModeCards.OrderBy(c => c.GroupRank).ToList();
        ModeCards.Clear();
        foreach (var c in ordered)
            ModeCards.Add(c);
    }

    /// <summary>
    /// The map grid is the selected mode's maps -- Windows' EnsureMapTiles, which
    /// builds the tiles and then decodes each one's thumbnail. The decode is the
    /// lazy half (<see cref="EnsureMapTiles"/>, run when the picker opens): a
    /// rebuilt grid is a fresh set of blank tiles whose art arrives a moment
    /// later.
    /// </summary>
    void FillMapTiles(ModeCardViewModel card)
    {
        MapTiles.Clear();
        foreach (var option in card.Maps)
            MapTiles.Add(new MapTileVM(option.Stem, option.DisplayName, option));

        var selected = MapTiles.FirstOrDefault(t => ReferenceEquals(t.Option, card.SelectedMap))
                       ?? MapTiles.FirstOrDefault();
        foreach (var t in MapTiles)
            t.IsSelected = ReferenceEquals(t, selected);
        // Deliberately not Map: that field is the advanced tab's own combo
        // (Windows' SelectedMap), and upstream keeps the two apart. The Simple
        // tab's launch pair comes off the card -- see SelectedPlayPair.
    }

    /// <summary>
    /// Port of MainWindow.RefreshHeroChrome: the title is the family's own name,
    /// the subtitle is the mode's map and blurb, and the command line is what the
    /// button would run. A mode that IS its arena would otherwise read its own
    /// name twice, so the map half is dropped when the two are equal.
    /// </summary>
    internal void RefreshHero()
    {
        var mode = SelectedMode;
        HeroTitle = mode?.Title ?? Loc.Get("pick_a_map");

        var map = string.Equals(mode?.SelectedMap?.DisplayName, mode?.Title,
                      StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : mode?.SelectedMap?.DisplayName ?? string.Empty;

        HeroSubtitle = mode is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(mode.Blurb) ? map
            : string.IsNullOrWhiteSpace(map) ? mode.Blurb
            : map + "  ·  " + mode.Blurb;

        var stem = mode?.SelectedMapStem;
        HeroCommand = mode is null || string.IsNullOrWhiteSpace(stem)
            ? string.Empty
            : $"+launchplaylist {mode.PlaylistId}   +map {stem}";

        // Windows' chrome pass and its loadscreen queue ride along with the hero
        // refresh (MainWindow.Loadscreen.cs:80-120), and every selection path in
        // this port ends here, so this is the one place that has to know.
        RefreshLoadscreenChrome();
        QueueLoadscreen();
    }

    /// <summary>
    /// The pair a Simple-mode launch runs: Windows reads it straight off the
    /// selected card (MainWindow.xaml.cs:4310-4321), which is why the rail is the
    /// one owner of what Play does. Advanced mode keeps its own combo pair.
    /// </summary>
    internal (string Playlist, string Map) SelectedPlayPair()
    {
        if (IsSimpleMode && SelectedMode is not null)
            return (SelectedMode.PlaylistId, SelectedMode.SelectedMapStem);
        return (Playlist, Map);
    }

    /// <summary>The lobby ships a stub loadscreen, so it borrows the art set
    /// instead. Windows' IsLobbyStem (MainWindow.Loadscreen.cs:374).</summary>
    static bool IsLobbyStem(string? stem) =>
        !string.IsNullOrWhiteSpace(stem) &&
        stem.Contains("lobby", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Port of MainWindow.CaptureModeSettingsFromUi: the rail's choice and every
    /// family's map are part of the saved settings, so the launcher reopens on
    /// the mode you left, on the map you left it on.
    /// </summary>
    internal void PersistModeSettings()
    {
        if (SelectedMode is not null)
            _s.LastModePlaylist = SelectedMode.Id;
        foreach (var card in ModeCards)
        {
            var stem = card.SelectedMapStem;
            if (!string.IsNullOrWhiteSpace(stem))
                _s.RememberMap(card.Id, stem);
        }
        try { _s.Save(); }
        catch (Exception ex) { AppendLog($"Settings save failed: {ex.Message}"); }
    }

    /// <summary>
    /// No playlist file yet, i.e. no install: the well-known Flowstate playlists,
    /// in the shape the loader would have produced. Windows has no equivalent --
    /// it sets status_no_modes and lets the installer fill the rail in -- but this
    /// launcher has to be usable before a 45 GB install, so the port's one
    /// deliberate addition lives here as *data*: everything downstream (cards,
    /// groups, tiles, hero, settings) sees an ordinary catalog.
    /// </summary>
    static IReadOnlyList<ModeFamily> PlaceholderFamilies(string defaultMap)
    {
        var map = string.IsNullOrWhiteSpace(defaultMap) ? LinuxSettings.DefaultMap : defaultMap.Trim();
        return new[]
        {
            new ModeFamily
            {
                Id = "survival_dev", Group = ModeGroups.Flowstate, Title = "Survival (dev)",
                Blurb = "Standard battle royale flow on the dev playlist.", TitleIsAuthored = true,
                Variants = new[]
                {
                    new ModeVariant { PlaylistId = "survival_dev", MapStem = map },
                    new ModeVariant { PlaylistId = "survival_dev", MapStem = "mp_rr_canyonlands_mu1" },
                },
            },
            new ModeFamily
            {
                Id = "survival", Group = ModeGroups.Flowstate, Title = "Survival",
                Blurb = "Standard battle royale flow.", TitleIsAuthored = true,
                Variants = new[]
                {
                    new ModeVariant { PlaylistId = "survival", MapStem = map },
                    new ModeVariant { PlaylistId = "survival", MapStem = "mp_rr_olympus_mu1" },
                },
            },
            new ModeFamily
            {
                Id = "1v1", Group = ModeGroups.Apex, Title = "1v1",
                Blurb = "Flowstate 1v1 versus arena.", TitleIsAuthored = true,
                Variants = new[]
                {
                    new ModeVariant { PlaylistId = "1v1", MapStem = "mp_rr_arena_composite" },
                    new ModeVariant { PlaylistId = "1v1", MapStem = "mp_rr_arena_habitat" },
                },
            },
        };
    }

    // ------------------------------------------------------------- catalog

    /// <summary>
    /// Read the install's playlists and maps. Windows' ReloadPlaylistsAndMaps is
    /// synchronous on the UI thread; this is the one place the port deliberately
    /// is not, because the loader walks the install tree for maps on disk (vpk,
    /// paks/Win64_server, paks/Win64, maps/, platform/maps, r2/maps) and this
    /// launcher's install may sit on a slow disk or a network mount — a
    /// half-second stall on a button press is the kind of thing this port exists
    /// to remove. The parse itself is upstream's, byte for byte.
    /// </summary>
    internal async Task ReloadCatalogAsync()
    {
        var root = InstallPath;
        var language = Loc.Code;
        var unlisted = ShowUnlistedMaps;

        PlaylistCatalog loaded;
        try
        {
            loaded = await Task.Run(() =>
                PlaylistCatalogLoader.Load(root, language, unlisted, Loc.Lookup)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Windows' own fallback: log it and carry on with an empty catalog,
            // so the rest of the shell keeps working.
            AppendLog($"Playlist load failed: {ex.Message}");
            loaded = new PlaylistCatalog();
        }

        _catalog = loaded;
        FillMapIds();
        RebuildModeCards();

        var source = _catalog.SourcePath is not null
            ? Path.GetFileName(_catalog.SourcePath)
            : "(no playlist file)";
        AppendLog($"Playlists reloaded: {source}, host={_catalog.HostPlaylists.Count}, "
            + $"file={_catalog.Entries.Count}, maps={_catalog.AllMaps.Count}");
    }

    /// <summary>
    /// The advanced card's map list, Windows' FillMapCombo: every map the
    /// playlists allow. The player's choice survives a reload when the new
    /// catalog still knows it, and otherwise the first map of the list takes
    /// over — the same rule Windows applies when its preferred map is gone.
    /// </summary>
    void FillMapIds()
    {
        var maps = _catalog.AllMaps;
        var keep = Map;

        MapIds.Clear();
        foreach (var map in maps)
            MapIds.Add(map);

        if (MapIds.Count == 0)
            return;

        Map = MapIds.Any(m => string.Equals(m, keep, StringComparison.OrdinalIgnoreCase))
            ? MapIds.First(m => string.Equals(m, keep, StringComparison.OrdinalIgnoreCase))
            : MapIds[0];
    }

    void BuildCredits()
    {
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "Flowstate", By = "CafeFPS",
            Blurb = "S21 bridge and features. Flowstate modes. Launcher and master server.",
            Url = "https://github.com/CafeFPS",
        });
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "R5Valkyrie", By = "kralrindo",
            Blurb = "Scripts, assets management and conversion, playtesting.",
            Url = "https://github.com/kralrindo",
        });
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "r5sdk", By = "Amos",
            Blurb = "The foundation. S3 listenserver sdk. Repak.",
            Url = "https://github.com/Mauler125/r5sdk",
        });
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "RSX / RePak", By = "r-ex",
            Blurb = "Conversion suite. Rmdlconv, bspconv, rsx, repak.",
            Url = "https://github.com/r-ex",
        });
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "R5-AnimConv", By = "someoneatemylastsliceofpizza",
            Blurb = "Animation rig and sequence converter.",
            Url = "https://github.com/someoneatemylastsliceofpizza/R5-AnimConv",
        });
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "Assets Formats Structs", By = "IJARika",
            Blurb = "Templates and structs for reSource games.",
            Url = "https://github.com/IJARika/resource_model_templates",
        });
        CreditsRows.Add(new CreditEntryVM
        {
            Title = "SERE", By = "RoyalBlue1",
            Blurb = "RUI assets editor.",
            Url = "https://github.com/RoyalBlue1/SERE",
        });
    }

    void BuildNotes()
    {
        // Cached/bundled only: the tab paints immediately off disk, and
        // RefreshNotesAsync replaces it once the channel is known.
        BindNotesLocal();
    }

    void BuildLanguages()
    {
        foreach (var row in NoticeLanguages.Picker)
            Languages.Add(new LanguageRow(row.Code, row.Label));
        SelectedLanguage = Languages.FirstOrDefault(l =>
            string.Equals(l.Code, Loc.Code, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
    }

    // NotesDocument / NotesEntry come from R5Flowstate.Contracts, where the
    // schema-1 wire shape (entries[], not notes[]) actually lives.

    // ------------------------------------------------------------------ helpers

    public void AppendLog(string line)
    {
        Log += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        Status = line;
        SimpleStatus = line;
        // The pane is not evidence: it dies with the window, and a launch that
        // failed after it closed leaves nothing to read. Everything the player
        // can see here is also written to ~/.config/r5flowstate/launcher.log.
        LauncherLog.Append(line);
    }

    /// <summary>The log alone, without the status line. Windows' Log() only
    /// appends; <see cref="AppendLog"/> is the port's shortcut for the two
    /// together, which is right for a launch's own progress and wrong for a
    /// periodic note like a heartbeat miss — the status line belongs to the
    /// launcher's state, not to a counter running in the background.</summary>
    internal void LogOnly(string line)
    {
        Log += $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        LauncherLog.Append(line);
    }

    /// <summary>AppendLog from a background thread (Wine output arrives on the
    /// process reader threads) — the log is bound, so it must be touched on the
    /// UI thread.</summary>
    void AppendLogThreadSafe(string line)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            AppendLog(line);
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(line));
    }

    /// <summary>Launch through Proton and report what actually happened. Wine
    /// writes launch failures to stderr and then exits (or runs headless), so a
    /// bare pid is not evidence of success: the child's output is streamed into
    /// the log, and a child that dies inside the grace window is reported as a
    /// failure with its own error text.
    ///
    /// The game's paths use <c>LaunchProtonTappedAsync</c> — their lines belong in
    /// the Console tab, not in a callback — and the EA paths call
    /// <see cref="LaunchWindowsAsync"/> with the runtime the settings name.</summary>

    /// <summary>The EA App this launcher started, held for as long as it runs and
    /// never killed: it is a background client the player keeps signed in while
    /// they play. Holding it is not bookkeeping — the readers that carry its lines
    /// into launcher.log live inside the process object, so dropping it would close
    /// the pipes of a client that is still talking.</summary>
    WindowsRun? _eaAppRun;

    /// <summary>The same spawn-and-report shape, for whichever runtime the launch
    /// plan names — so an EA App under the system Wine is diagnosed with exactly
    /// the words a Proton client is.
    ///
    /// It returns the run rather than a boolean, and it deliberately does not
    /// dispose it: the process's redirected readers are what keep the child's own
    /// words arriving in the log, and closing them when this method returns is
    /// what silenced the EA installer fifteen seconds into an attempt that had
    /// five minutes left to run (2026-09-22) — taking the stderr of the failing
    /// MSI apply with it. A run whose process has already exited is still a run,
    /// and that is the second half of the same lesson: on 2026-09-22 05:01 the
    /// installer ran Burn's whole cache-and-apply cycle, failed at EA's 1603 and
    /// rolled back in nine seconds, and because it did all of that inside the
    /// grace window this method threw the capture away and the report could only
    /// say "nothing in the captured output names the step that failed" — while
    /// the line naming the action sat in launcher.log. Fast is not the same as
    /// absent, so there is no null return left to read as failure.</summary>
    async Task<WindowsRun> LaunchWindowsAsync(ProtonLauncher.ProtonRunOptions options, string label,
        string runningNote, int graceSeconds = 15)
    {
        var started = DateTime.UtcNow;
        var lines = new System.Collections.Generic.List<string>();
        var gate = new object();
        var p = ProtonLauncher.StartCaptured(options, line =>
        {
            lock (gate)
            {
                lines.Add(line);
                // The ring is generous because the diagnosis is not always in the
                // last few lines: Wine's MSI halts, and then the installer's own
                // embedded browser keeps complaining for thirty lines after it.
                if (lines.Count > WindowsRun.CaptureLimit)
                    lines.RemoveAt(0);
            }
            AppendLogThreadSafe($"{label}: {line}");
        });

        IReadOnlyList<string> Lines() { lock (gate) return new List<string>(lines); }

        var exitTask = p.WaitForExitAsync();
        var done = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(graceSeconds)));
        if (ReferenceEquals(done, exitTask))
        {
            // Drain the output readers so nothing is lost to the race with exit.
            try { await exitTask; } catch { }
            var code = p.ExitCode;
            var ran = DateTime.UtcNow - started;
            var tail = Lines();
            var gl = tail.Any(ProtonLauncher.LooksLikeGlFailure);
            AppendLog(EaInstallWatch.EarlyExitNote(label, ran, code));
            if (tail.Count == 0)
                AppendLog($"{label}: no output. Check the runtime and the prefix.");
            if (gl)
                AppendLog($"{label}: Wine could not create an OpenGL context (GLX), so its "
                          + "window can never appear. Verify with `glxinfo -B`; an X11 "
                          + "session or a working XWayland GLX is required.");

            // Handed back, not thrown away: the caller decides what a fast exit
            // means (for an installer, that is a watcher's question, and it can
            // answer it only if it can still see this child's own words).
            return new WindowsRun(p, Lines);
        }

        if (!p.HasExited)
            AppendLog(runningNote);
        return new WindowsRun(p, Lines);
    }

    /// <summary>Setup-panel size line. The line reads "checking" only while the
    /// channel probe is genuinely in flight; it settles on the Windows launcher's
    /// own disk copy (disk_about / disk_unknown / disk_unread) and must never park
    /// on the checking placeholder.</summary>
    [ObservableProperty] private string _installSizeText = Loc.Get("checking_size");

    /// <summary>Resolve the size line for the current install path, off the UI
    /// thread. The numbers come from the channel manifest plus the planner, which
    /// is the same source upstream paints from — not from walking the install tree.</summary>
    public async Task RefreshInstallSizeAsync()
    {
        if (_contentBusy) return;

        InstallSizeText = Loc.Get("checking_size");
        var root = InstallPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            InstallSizeText = Loc.Get("disk_unknown");
            return;
        }

        if (await LoadChannelAsync().ConfigureAwait(true) is null)
        {
            InstallSizeText = Loc.Get("disk_unknown");
            return;
        }

        var (text, danger) = await Task.Run(() => InstallDiskLine(_channel, InstallPath)).ConfigureAwait(true);
        InstallSizeText = text;
        if (danger) SimpleStatus = text;

        // Paint the same health line the check-updates action does, so the card
        // says where the install stands as soon as the channel resolves.
        if (_channel is not null)
        {
            HealthText = HealthLine(Assess(_channel, InstallPath));
            InstallStatus = HealthText;
        }
    }

    partial void OnPlaylistChanged(string value) => UpdatePreviews();
    partial void OnMapChanged(string value) => UpdatePreviews();
    partial void OnPortChanged(int value) => UpdatePreviews();
    partial void OnDevChanged(bool value) => UpdatePreviews();
    partial void OnOfflineNoAuthChanged(bool value) => UpdatePreviews();
    partial void OnHostOnlineChanged(bool value) => UpdatePreviews();
    partial void OnCheatsChanged(bool value) => UpdatePreviews();
    partial void OnClientArgsChanged(string value) => UpdatePreviews();
    partial void OnDediArgsChanged(string value) => UpdatePreviews();
    partial void OnDediPasswordChanged(string value) => UpdatePreviews();
    partial void OnClientExtraChanged(string value) => UpdatePreviews();
    partial void OnDediExtraChanged(string value) => UpdatePreviews();

    void UpdatePreviews()
    {
        var pw = PasswordProtect && !string.IsNullOrWhiteSpace(DediPassword) ? DediPassword : null;
        try
        {
            var dedi = LaunchArgBuilder.BuildDedi(Dev, OfflineNoAuth, HostOnline, Port, Playlist, Map, pw, Cheats, DediArgs);
            DediPreview = string.Join(' ', dedi);
            var client = LaunchArgBuilder.BuildClient(Dev, OfflineNoAuth, false, "english", ClientArgs, ClientWindowMode,
                ClientWidth, ClientHeight, null, null, $"127.0.0.1:{Port}");
            ClientPreview = string.Join(' ', client);
        }
        catch { }
        RefreshHero();
    }

    [RelayCommand]
    private void SelectMode(ModeItemVM? mode)
    {
        if (mode is null) return;
        SelectModeCard(mode.Card, persist: true);
        AppendLog($"Mode: {mode.Title} ({mode.Id})");
    }

    /// <summary>
    /// Windows' OnMapTileClick: the tile you picked becomes the selected mode's
    /// map, the choice is remembered for that family, and the tile you picked is
    /// the one that reads as selected. The dedi map field follows, because the
    /// Simple tab's Play runs exactly that pair.
    /// </summary>
    [RelayCommand]
    private void SelectMap(MapTileVM? tile)
    {
        if (tile is null) return;
        foreach (var t in MapTiles)
            t.IsSelected = ReferenceEquals(t, tile);
        if (SelectedMode is not null && tile.Option is not null)
            SelectedMode.SelectedMap = tile.Option;
        PersistModeSettings();
        RefreshHero();
        AppendLog($"Map: {tile.DisplayName} ({tile.Stem})");
    }

    [RelayCommand]
    private void RefreshMods()
    {
        ModsStatus = "Mods live under <install>/mods — same layout as Windows.";
        AppendLog("Mods refresh: <install>/mods scan lands with the content installer.");
    }

    [RelayCommand]
    private void SetLanguage(LanguageRow? row)
    {
        if (row is null) return;
        Loc.SetLanguage(row.Code);
        // The {loc:T} bindings refresh off Loc.Instance; the install card's three
        // strings are computed from the busy flags and read Loc directly, so they
        // are the one thing a language change has to announce itself.
        NotifyLocalizedInstallStrings();
        _s.UiLanguage = row.Code;
        _s.Save();
        AppendLog($"Language: {row.Label}");
    }

    // ------------------------------------------------------------ legal notice
    // Windows parity: SettingsStore.EulaVersionAccepted / EulaLanguage. The
    // Servers tab gate reads these, so the notice only has to be accepted once
    // per version.

    /// <summary>Highest notice version the player has accepted. 0 = never.</summary>
    public int EulaVersionAccepted => _s.EulaVersionAccepted;

    /// <summary>Language to fetch the notice in (UI pick, else last accepted, else OS).</summary>
    public string EulaUiLanguage() => NoticeLanguages.ResolveUi(_s.UiLanguage, _s.EulaLanguage);

    public void SaveEulaAccepted(int version)
    {
        _s.EulaVersionAccepted = version;
        try { _s.Save(); }
        catch (Exception ex) { AppendLog("Settings save failed: " + ex.Message); }
    }

    /// <summary>The notice language doubles as the UI language, like Windows.</summary>
    public void RememberEulaLanguage(string code)
    {
        var ui = NoticeLanguages.ForUi(code);
        _s.UiLanguage = ui;
        _s.EulaLanguage = ui;
        Loc.SetLanguage(ui);
        try { _s.Save(); }
        catch
        {
            // keep the in-memory pick
        }
    }

    [RelayCommand]
    private void RefreshProtons()
    {
        var list = ProtonDiscovery.Discover();
        Protons = new ObservableCollection<ProtonEntry>(list);
        if (!string.IsNullOrWhiteSpace(ProtonDir))
            SelectedProton = list.FirstOrDefault(p => p.Dir == ProtonDir);
        SelectedProton ??= ProtonDiscovery.PickLatest(list);
        if (SelectedProton is not null)
        {
            ProtonDir = SelectedProton.Dir;
            AppendLog($"Proton: {SelectedProton.Name} ({SelectedProton.Version})");
        }
        else if (AutoDownloadProtonGe)
            AppendLog("No Proton found under Steam. Install flow will fetch Proton-GE.");
        else
            AppendLog("No Proton found under Steam, and the automatic Proton-GE download is off. "
                      + "Press Download Proton-GE in Settings to fetch it.");
        UpdateEaStatus();
    }

    /// <summary>First-run gate: if the EA App is absent from the game's prefix, raise
    /// the popup. There is one prefix, so there is one place to look.</summary>
    private void CheckEaFirstRun()
    {
        try
        {
            var prefix = EaPrefixFor();
            if (string.IsNullOrWhiteSpace(prefix)) return;
            if (!EaIsInstalled())
            {
                var runtime = SelectedProton?.Name ?? "auto (Proton-GE fallback)";
                EaDialogText =
                    "EA App was not detected in this Proton prefix.\n\n" +
                    $"Prefix: {prefix}\nRuntime: {runtime}\n\n" +
                    "Press Install to download the EA App installer from the official EA website " +
                    "and set it up automatically. " +
                    "If no Proton build is available, Proton-GE will be downloaded first.";
                ShowEaDialog = true;
                AppendLog("EA App not detected — showing first-run setup popup.");
            }
        }
        catch (Exception ex) { AppendLog("EA check failed: " + ex.Message); }
    }

    [RelayCommand]
    private void DismissEaDialog() => ShowEaDialog = false;

    [RelayCommand]
    private async Task InstallEaFromDialog()
    {
        ShowEaDialog = false;
        await InstallEa();
    }

    [RelayCommand]
    private void UpdateEaStatus()
    {
        try
        {
            var prefix = EaPrefixFor();
            if (string.IsNullOrWhiteSpace(prefix))
            {
                EaStatus = "no prefix set";
                return;
            }
            var exe = ProtonLauncher.FindEaDesktopExe(prefix);
            EaStatus = exe is not null
                ? $"installed in the game prefix: {exe}"
                : "not installed in the game prefix";
        }
        catch (Exception ex) { EaStatus = "error: " + ex.Message; }
    }

    /// <summary>
    /// Connect-test EA's loopback ports and say what answered. The card's own
    /// version of <c>--ea-probe</c>, and the only way to tell "EA is running" from
    /// "EA is running <em>and</em> the port the game dials is open" before a join
    /// goes into a handshake hold.
    ///
    /// Reads only, spawns nothing: a connect to two ports, on both families, with a
    /// 400 ms budget. Off the UI thread because even a refusal is a syscall.
    /// </summary>
    [RelayCommand]
    private async Task ProbeEaChannel()
    {
        try
        {
            EaChannelStatus = "probing 3216/3215…";
            var probes = await Task.Run(() => EaChannelProbe.Probe());
            var (line, listening) = EaChannelProbe.Verdict(probes);
            EaChannelStatus = line;
            foreach (var probe in probes)
                LogOnly($"channel: {probe.Describe()} — {probe.Detail}");
            UpdateEaStatus();
            if (listening)
                AppendLog("Something is listening on EA's LSX port. That is necessary, not "
                          + "sufficient: the handshake above it needs EA's own keys, so only "
                          + "the game's log can say whether identity arrived.");
        }
        catch (Exception ex) { EaChannelStatus = "probe failed: " + ex.Message; }
    }

    /// <summary>
    /// The online pre-flight: before a client that has to prove an account is
    /// started, EA must be reachable. Returns false with the reason already in the
    /// log when it is not, because the alternative — launching into
    /// <c>[NET-OBS] HANDSHAKE hold</c> — is a client that looks like it is joining
    /// and never will.
    ///
    /// Offline launches are not gated: nothing on that path needs EA, and the
    /// install's own <c>autoexec_client_dev.cfg</c> already sets
    /// <c>origin_disconnectWhenOffline "0"</c>.
    /// </summary>
    async Task<bool> EnsureEaChannelForOnlineAsync(string action)
    {
        try
        {
            if (OfflineNoAuth)
            {
                EaBridgeStatus = "identity: offline launch — EA is not needed";
                return true;
            }

            var exe = ProtonLauncher.FindEaDesktopExe(EaPrefixFor());
            Func<System.Diagnostics.Process>? start = null;

            if (StartEaWithGame && exe is not null)
            {
                start = () =>
                {
                    var plan = EaPlan(exe, workingDirectory: Path.GetDirectoryName(exe) ?? "");
                    AppendLog($"Starting the EA App via {ProtonLauncher.Describe(plan)}…");
                    _eaProcess = ProtonLauncher.StartCaptured(plan,
                        line => AppendLogThreadSafe("EA App: " + line));
                    return _eaProcess;
                };
            }
            else if (exe is not null && !StartEaWithGame)
            {
                AppendLog("EA is not started automatically (Start the EA App with the game is off) "
                          + "— checking whether it is already running.");
            }

            var report = await EaBridge.EnsureAsync(exe, start, AppendLog);
            EaBridgeStatus = report.Line;
            if (report.CanJoin)
            {
                WatchEaBridge(report, start);
                AppendLog($"EA channel up: {report.Line}");
                return true;
            }

            AppendLog($"{action} refused: {report.Line}");
            return false;
        }
        catch (Exception ex)
        {
            EaBridgeStatus = "identity: the EA channel check failed: " + ex.Message;
            AppendLog($"{action} refused: " + EaBridgeStatus);
            return false;
        }
    }

    /// <summary>
    /// Keep an eye on a channel that was up, while a client is running. The socket
    /// cannot see this failure — a dead EA App can leave a listener behind — and it
    /// is the one that turns into "connected, but the server will not accept me".
    /// One bounded relaunch, and only of a child this launcher started.
    /// </summary>
    void WatchEaBridge(EaBridgeReport report, Func<System.Diagnostics.Process>? start)
    {
        _eaWatcher = new EaBridgeWatcher(report, start, AppendLogThreadSafe);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (_eaWatcher is null || _eaWatcher.Dead || _tapClient is null)
                    return;

                var state = _eaWatcher.Poll();
                Invoke(() => EaBridgeStatus = $"identity: {EaBridge.Describe(state)}"
                                              + (_eaWatcher.LastLine is null ? "" : " — " + _eaWatcher.LastLine));
                if (state == EaBridgeState.Died)
                    return;
            }
        });
    }

    /// <summary>Progress sink for downloads. Marshals to the UI thread and
    /// throttles the text so a fast transfer cannot flood the dispatcher.</summary>
    /// <remarks>The bridge button that used to sit above this is gone: it existed to
    /// copy an EA install from the Wine prefix into the game's Proton prefix, and with
    /// one prefix the registry EA writes is the registry the game reads. The button
    /// said exactly that before refusing, which is why nothing replaces it.</remarks>
    IProgress<DownloadProgress> DownloadProgressSink() => new DownloadProgressRelay(this);

    sealed class DownloadProgressRelay : IProgress<DownloadProgress>
    {
        readonly MainViewModel _vm;
        long _lastTick;

        public DownloadProgressRelay(MainViewModel vm) => _vm = vm;

        public void Report(DownloadProgress p)
        {
            var now = Environment.TickCount64;
            // ~6 updates a second is plenty for a status line.
            if (p.Fraction is not null && now - _lastTick < 160)
                return;
            _lastTick = now;

            var text = p.Describe();
            var fraction = p.Fraction ?? 0;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _vm.DownloadStatus = text;
                _vm.DownloadFraction = fraction;
            });
        }
    }

    /// <summary>Downloads the EA App installer without installing it. The file is
    /// validated (PE header + size) before it is accepted, and an already-valid
    /// copy in ~/Downloads is reused rather than overwritten.</summary>
    [RelayCommand]
    private async Task DownloadEaInstaller()
    {
        if (Downloading) return;
        Downloading = true;
        DownloadStatus = "contacting EA…";
        try
        {
            var path = await EaInstaller.EnsureDownloadedAsync(AppendLog, DownloadProgressSink());
            var info = EaInstaller.ReadInfo(path);
            DownloadStatus = $"EA installer ready ({new FileInfo(path).Length / 1048576d:0.0} MB)";
            AppendLog($"EA installer ready at {path}" +
                      (info is null || info.Sha256.Length == 0 ? "" : $" — sha256 {info.Sha256}"));
        }
        catch (Exception ex)
        {
            DownloadStatus = "EA installer download failed.";
            AppendLog("EA installer download failed: " + ex.Message);
        }
        finally
        {
            Downloading = false;
            DownloadFraction = 0;
        }
    }

    /// <summary>Downloads and extracts the newest Proton-GE into
    /// compatibilitytools.d, so a box with no Steam Proton still has a runtime.</summary>
    [RelayCommand]
    private async Task DownloadProton()
    {
        if (Downloading) return;
        Downloading = true;
        DownloadStatus = "querying the GE-Proton release…";
        try
        {
            var dir = await ProtonManager.DownloadLatestGeProtonAsync(AppendLog, DownloadProgressSink());
            RefreshProtons();
            DownloadStatus = $"Proton-GE ready: {Path.GetFileName(dir)}";
            AppendLog($"Proton-GE ready at {dir}");
        }
        catch (Exception ex)
        {
            DownloadStatus = "Proton-GE download failed.";
            AppendLog("Proton-GE download failed: " + ex.Message);
        }
        finally
        {
            Downloading = false;
            DownloadFraction = 0;
        }
    }

    [RelayCommand]
    private void Save()
    {
        _s.InstallPath = InstallPath; _s.PrefixPath = PrefixPath; _s.ProtonDir = ProtonDir;
        _s.StartEaWithGame = StartEaWithGame;
        _s.AutoDownloadProtonGe = AutoDownloadProtonGe;
        _s.DediPlaylist = Playlist; _s.DediMap = Map; _s.DediPort = Port;
        _s.OfflineNoAuth = OfflineNoAuth; _s.DediHostOnline = HostOnline;
        _s.DevProfile = Dev; _s.Cheats = Cheats; _s.UseDx12 = UseDx12;
        _s.ClientLaunchArguments = ClientArgs; _s.DediLaunchArguments = DediArgs;
        _s.DediPassword = DediPassword; _s.DediPasswordEnabled = PasswordProtect;
        _s.ClientWidth = ClientWidth; _s.ClientHeight = ClientHeight;
        _s.ClientWindowMode = ClientWindowMode;
        _s.DownloadLimitMbps = DownloadLimit;
        _s.OpenConsoleOnLaunch = OpenConsoleOnLaunch;
        _s.ShowUnlistedMaps = ShowUnlistedMaps;
        _s.JoinWithoutDev = JoinWithoutDev;
        _s.KeepLocalFiles = KeepLocalFiles;
        _s.SimpleMode = IsSimpleMode;
        _s.Save();
        AppendLog("Settings saved to ~/.config/r5flowstate/settings.json");
        UpdateEaStatus();
    }

    private bool RequireProton() => SelectedProton is not null || !string.IsNullOrWhiteSpace(ProtonDir);
    private string EffectiveProtonDir() => SelectedProton?.Dir ?? ProtonDir;

    // ---------------------------------------------------------------- EA App

    /// <summary>
    /// The prefix the EA App lives in. It is the game's prefix, always, and this is
    /// the one place that says so: everything EA reads — the desktop exe, its logs,
    /// its install state, its registry — goes through here rather than through
    /// PrefixPath spelled out at each call site.
    ///
    /// <para>It used to be a choice: the system Wine in a prefix of its own, so the
    /// two Wine servers could not interfere. That turned out to be unnecessary as
    /// well as unsupported — EA and the game meet on the host's loopback (EA's LSX
    /// server on 127.0.0.1:3216), which crosses prefixes — and sharing a prefix is
    /// what makes in-game auth work without copying registry keys between two
    /// prefixes. The property stays because it is the seam, not because it varies.
    /// </para>
    /// </summary>
    internal string EaPrefixFor() => PrefixPath;

    internal bool EaIsInstalled() => ProtonLauncher.IsEaInstalled(EaPrefixFor());

    internal string? EaNewestLog() => ProtonLauncher.FindNewestEaLog(EaPrefixFor());

    /// <summary>The launch plan for an EA program: which prefix and which binary.
    /// <paramref name="exe"/> is the Linux path to a Windows exe.</summary>
    internal ProtonLauncher.ProtonRunOptions EaPlan(string exe, string args = "",
        string? workingDirectory = null)
        => new(
            EffectiveProtonDir(),
            EaPrefixFor(),
            exe,
            args,
            WorkingDirectory: workingDirectory ?? EaPrefixFor());

    /// <summary>Typing a different prefix changes where EA is looked for, so the
    /// status line has to follow it — otherwise the card says "not installed" about
    /// the old folder while the box shows the new one. Not persisted here: the
    /// path is a text field, and the Save button is what writes it down.</summary>
    partial void OnPrefixPathChanged(string value)
    {
        EaChannelStatus = "";
        UpdateEaStatus();
    }

    private string JoinArgs(string[] args) =>
        string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    private string ClientExe()
    {
        var root = InstallPath;
        var exe = Path.Combine(root, UseDx12 ? "r5apex_dx12.exe" : "r5apex.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "game", UseDx12 ? "r5apex_dx12.exe" : "r5apex.exe");
        return exe;
    }

    private string DediExe()
    {
        var root = InstallPath;
        var exe = Path.Combine(root, "r5apex_ds.exe");
        if (!File.Exists(exe)) exe = Path.Combine(root, "game", "r5apex_ds.exe");
        return exe;
    }

    [RelayCommand]
    private async Task LaunchClient()
    {
        try
        {
            if (!RequireProton()) { AppendLog("Pick a Proton build first."); return; }
            if (!await EnsureEaChannelForOnlineAsync("Client launch")) return;
            var exe = ClientExe();
            var clientArgs = LaunchArgBuilder.BuildClient(Dev, OfflineNoAuth, false, "english", ClientArgs, ClientWindowMode,
                ClientWidth, ClientHeight, null, null, $"127.0.0.1:{Port}");
            // A launch of ours replaces whatever we were joined to.
            JoinedRemote = false;
            await LaunchProtonTappedAsync(
                new(EffectiveProtonDir(), PrefixPath, exe, JoinArgs(clientArgs.ToArray()), InstallPath),
                LaunchRole.Client,
                "Client", $"Client started via Proton. Connect target 127.0.0.1:{Port}.");
        }
        catch (Exception ex) { AppendLog("Client launch failed: " + ex.Message); }
    }

    [RelayCommand]
    private async Task LaunchDedi()
    {
        try
        {
            if (!RequireProton()) { AppendLog("Pick a Proton build first."); return; }
            var pw = PasswordProtect && !string.IsNullOrWhiteSpace(DediPassword) ? DediPassword : null;
            var dediArgs = LaunchArgBuilder.BuildDedi(Dev, OfflineNoAuth, HostOnline, Port, Playlist, Map, pw, Cheats, DediArgs);

            // A dedi launched on its own still has a ready line, and the console
            // taps it either way; the gate is opened so the line is classified
            // (and the live map tracked) exactly as it is for a local play.
            JoinedRemote = false;
            SetLivePair(Playlist, Map);
            var rcon = OpenRcon(Port);
            OpenHostGate();
            await LaunchProtonTappedAsync(
                new(EffectiveProtonDir(), PrefixPath, DediExe(), JoinArgs(dediArgs.ToArray()), InstallPath,
                    ExtraEnv: rcon.ToEnvironment()),
                LaunchRole.Dedicated,
                "Dedi", $"Dedi started via Proton on port {Port}.");
        }
        catch (Exception ex)
        {
            DropLocalRcon();
            AppendLog("Dedi launch failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The kill, as a seam — and the suite only ever uses it that way.
    ///
    /// <para>What the three stop paths <em>decide</em> (which roles, which root,
    /// which children this launcher owns) is what the suite reads back here. What
    /// it must never do is perform the sweep: it is scoped to the install root and
    /// matched by image name, but "the suite does not kill a process it did not
    /// start" is a stronger rule than "it is unlikely to match the player's game",
    /// and that is the rule this codebase keeps. The sweep's own rules are checked
    /// directly instead (<c>GameProcesses.NamesImage</c>, <c>IsUnderRoot</c>,
    /// <c>ImagesFor</c>, and a read-only scan for an image nothing can carry).</para>
    /// </summary>
    internal static Func<string?, IReadOnlyList<LaunchRole>, IReadOnlyList<System.Diagnostics.Process?>, int>?
        KillOverride;

    /// <summary>
    /// The children this launcher owns: PLAY's dedi and client, which the consoles
    /// hold taps to. They go first, as trees, because they are the only processes
    /// here nobody else has to be consulted about — a tap's child is the
    /// <c>proton run</c> the launcher started, so its tree is the wineserver and
    /// the game as well.
    /// </summary>
    IReadOnlyList<System.Diagnostics.Process?> TrackedChildren() => new[]
    {
        TapFor(LaunchRole.Dedicated)?.Child,
        TapFor(LaunchRole.Client)?.Child,
    };

    int Kill(string? root, IReadOnlyList<LaunchRole> roles) =>
        Kill(root, roles, TrackedChildren());

    int Kill(string? root, IReadOnlyList<LaunchRole> roles,
        IReadOnlyList<System.Diagnostics.Process?> tracked) =>
        KillOverride is { } seam
            ? seam(root, roles, tracked)
            : GameProcesses.KillRoles(roles, root, tracked);

    /// <summary>
    /// Windows' OnKillClient: the client's processes, both consoles, and nothing
    /// else. A dedi of ours keeps running — that is what KillDedi is for, and
    /// upstream keeps the two apart for exactly that reason.
    /// </summary>
    [RelayCommand]
    private void KillClient()
    {
        JoinedRemote = false;
        DisarmHostedMatch();
        var n = Kill(InstallPath, new[] { LaunchRole.Client });
        // Both panes go, as they do on Windows (DropHostedConsoles drops both): the
        // client's output stops arriving, and a console whose process is gone is not
        // live either way. What each pane already shows stays — that is the record
        // of the run, and the player may still be reading it.
        DropConsoles();
        AppendLog($"Kill Client: stopped {n} process(es)");
    }

    /// <summary>
    /// Windows' OnKillDedi: the dedi's processes, RCON, and both hosted consoles —
    /// no host, no session.
    /// </summary>
    [RelayCommand]
    private void KillDedi()
    {
        JoinedRemote = false;
        DisarmHostedMatch();
        var n = Kill(InstallPath, new[] { LaunchRole.Dedicated });
        // No host, no session: upstream drops the RCON with the process, and the
        // live pair with it. A launch waiting on this host is now waiting for a
        // server that will never answer, and DropConsoles ends that wait
        // (`_hostWaitCts`), which is the other half of what upstream cancels.
        DropLocalRcon();
        DropConsoles();
        AppendLog($"Kill Dedi: stopped {n} process(es)");
    }

    /// <summary>
    /// Windows' StopSessionAsync, in its own order: the wait for a host is
    /// cancelled first so nothing new starts behind the teardown, the match is
    /// disarmed, every process of <em>both</em> roles under the install root is
    /// stopped, and only then does the session's own state go — the join, RCON,
    /// both consoles, the status line.
    ///
    /// <para>This is what the console tab's STOP runs. It was bound to the dedi's
    /// own kill, which stopped one role, left the client running, and left the
    /// session standing — so the button did not do what it said, which is what the
    /// player reported.</para>
    ///
    /// <para>One thing upstream does here that this cannot: <c>RestoreSessionMods</c>.
    /// Windows puts the player's mod policy back on STOP; this port has no session
    /// mod policy to put back, so there is nothing to restore — the mods lane is a
    /// separate, older piece of the port and is not this button's business.</para>
    /// </summary>
    [RelayCommand]
    private async Task StopSession()
    {
        try
        {
            _hostWaitCts?.Cancel();
            DisarmHostedMatch();

            var root = InstallPath;
            var roles = new[] { LaunchRole.Client, LaunchRole.Dedicated };
            var tracked = TrackedChildren();
            var n = await Task.Run(() => Kill(root, roles, tracked)).ConfigureAwait(true);

            JoinedRemote = false;
            DropLocalRcon();
            DropConsoles();

            AppendLog($"Simple STOP: killed {n} process(es)");
            SimpleStatus = Loc.Get("status_stopped");
            // The chrome follows on its own: the Reload button and the two pickers
            // are offered for a host this launcher steers, and DropLocalRcon raises
            // HostSteeringReady, which is what the window repaints them from.
        }
        catch (Exception ex) { AppendLog("STOP failed: " + ex.Message); }
    }

    /// <summary>
    /// A size picked from the Res menu (or typed into the boxes and committed on
    /// LostFocus). Replaces the five-preset cycler the first port had: the menu now
    /// offers what the session actually lists, grouped by aspect, exactly like
    /// Windows' Res button.
    ///
    /// 0 x 0 is the menu's "Game default" item and means no <c>-width</c>/
    /// <c>-height</c> at all — the state this launcher has always had, and the only
    /// way to hand the choice back to the game once a size has been set. A single
    /// dimension outside [320, 16384] takes the whole pair back to that, rather than
    /// launching one of the two numbers that was typed and ignoring the other.
    /// </summary>
    public void SetResolution(int width, int height)
    {
        var w = DisplayModes.ClampDimension(width, 0);
        var h = DisplayModes.ClampDimension(height, 0);
        if (w != width || h != height)
        {
            AppendLog($"Resolution {width}x{height} is outside "
                + $"{DisplayModes.MinDimension}–{DisplayModes.MaxDimension}; using "
                + (w == 0 || h == 0 ? "the game's own" : $"{w}x{h}") + ".");
        }

        ClientWidth = w;
        ClientHeight = h;
        AppendLog(w == 0 || h == 0
            ? "Resolution override off (game default)."
            : $"Resolution override: {w}x{h}");
        UpdatePreviews();
        PersistClientDisplay();
    }

    /// <summary>
    /// A window mode picked from the Res menu. Always one of Windowed / Borderless /
    /// Fullscreen — <see cref="DisplayModes.ClampWindowMode"/> decides, and the menu
    /// only ever offers those three, so the only way to arrive here with anything
    /// else is a caller bug and it lands on fullscreen rather than on no flag.
    /// </summary>
    public void SetClientWindowMode(string mode)
    {
        var chosen = DisplayModes.ClampWindowMode(mode);
        ClientWindowMode = chosen;
        AppendLog($"Client window mode: {DisplayModes.ModeLabel(chosen)}");
        AppendLog(FullscreenNote(chosen, ClientWidth, ClientHeight));
        UpdatePreviews();
        PersistClientDisplay();
    }

    /// <summary>
    /// Windows' LogFullscreenPresentation, in one line: whether the size that will
    /// be asked for is one the session lists, one it will scale, or one bigger than
    /// the desktop (which is what a fullscreen request the driver cannot honour
    /// means in practice). Only worth saying for fullscreen; the other two modes are
    /// already windowed and the flag speaks for itself.
    /// </summary>
    string FullscreenNote(string mode, int width, int height)
    {
        if (mode != DisplayModes.Fullscreen)
            return $"Client window mode: {DisplayModes.ModeLabel(mode)}, {DisplayModes.Describe(width, height)}";
        if (width <= 0 || height <= 0)
            return "Fullscreen: the game picks the size (no override).";
        if (DisplayModes.IsListedDisplayMode(width, height))
            return $"Fullscreen {width}x{height}: exact (this display lists it)";
        var desk = DisplayModes.Desktop();
        if (desk.Width > 0 && width <= desk.Width && height <= desk.Height)
            return $"Fullscreen {width}x{height}: fits the desktop, so the display will scale it";
        return $"Fullscreen {width}x{height}: larger than the desktop — expect a borderless picture";
    }

    /// <summary>
    /// The two display settings, written as soon as they are picked. Windows does the
    /// same (<c>SettingsStore.Save</c> inside CommitClientWindowMode and the
    /// resolution commit): a mode that only survives if the player launches something
    /// afterwards is a mode the player has to set twice.
    /// </summary>
    void PersistClientDisplay()
    {
        _s.ClientWidth = ClientWidth;
        _s.ClientHeight = ClientHeight;
        _s.ClientWindowMode = ClientWindowMode;
        try { _s.Save(); }
        catch (Exception ex) { AppendLog("Settings save failed: " + ex.Message); }
    }

    public void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { AppendLog("Open failed: " + ex.Message); }
    }

    // Content-installer actions live in MainViewModel.Install.cs: CheckUpdates,
    // InstallGame, Verify and RepairFiles are the real channel-manifest pipeline
    // (R5Flowstate.Content), not stubs.

    [RelayCommand]
    private void RemoveGameFiles()
    {
        AppendLog("Remove game files: disabled in this test build — delete the folder manually if needed.");
    }

    [RelayCommand]
    private async Task ReloadPlaylists()
    {
        // The Advanced tab's own "Reload playlists/maps" button. Windows passes
        // selectSaved: false here (the dedi combos keep whatever is selected and
        // the saved pair is only re-applied at startup), which is what
        // FillMapIds does by preferring the current pick.
        await ReloadCatalogAsync();
        AppendLog(_catalog.Entries.Count == 0
            ? "Reload playlists: no playlist file under " + InstallPath
            : $"Reload playlists: {_catalog.AllMaps.Count} map(s) available.");
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) { AppendLog($"Folder does not exist: {path}"); return; }
        OpenUrl(path);
    }

    [RelayCommand]
    private async Task PlayLocal()
    {
        try
        {
            Save();
            if (!RequireProton()) { AppendLog("Pick a Proton build first."); return; }
            var root = InstallPath;
            if (!InstallPresence.GamePresent(root))
            {
                // What is actually missing, and where to go instead: a root can
                // hold the platform lane and no game (see InstallPresence), and
                // "missing r5apex.exe" said that badly.
                AppendLog($"Nothing to play in {root}: the game is not installed there.");
                SimpleStatus = Loc.Get("status_install_needed_to_play");
                return;
            }

            // The one case where launching is the wrong answer: the install is
            // behind and the lane that would deliver the update is open. Windows
            // gates its smart PLAY button on exactly this pair (health.NeedsUpdate
            // + LaneBlocksPlay) and lets the launch through when the lane is shut.
            if (_channel is not null)
            {
                var health = Assess(_channel, root);
                if (health.Enforced && health.NeedsUpdate)
                {
                    var gate = await RefreshDownloadGateAsync().ConfigureAwait(true);
                    if (LaneBlocksPlay(health, gate, requireClient: true, requireServer: false))
                    {
                        var line = Loc.Format("gate_update_first", health.Summary);
                        SimpleStatus = line;
                        InstallStatus = line;
                        AppendLog("PLAY refused: " + line.Replace('\n', ' '));
                        return;
                    }
                    AppendLog("Update available but downloads are off; allowing play.");
                }
            }

            var proton = EffectiveProtonDir();
            var pw = PasswordProtect && !string.IsNullOrWhiteSpace(DediPassword) ? DediPassword : null;
            var (playPlaylist, playMap) = SelectedPlayPair();
            AppendLog($"Simple pair: +launchplaylist {playPlaylist} +map {playMap}");

            // The session exists before the dedi does, because the dedi's own
            // environment is what tells it to open the loopback listener (and
            // the client that joins it gets the same three variables, so its
            // console can reach the server too).
            var rcon = OpenRcon(Port);
            var rconEnv = rcon.ToEnvironment();
            SetLivePair(playPlaylist, playMap);
            var dediArgs = LaunchArgBuilder.BuildDedi(Dev, OfflineNoAuth, HostOnline, Port, playPlaylist, playMap, pw, Cheats, DediArgs);
            var clientArgs = LaunchArgBuilder.BuildClient(Dev, OfflineNoAuth, false, "english", ClientArgs, ClientWindowMode,
                ClientWidth, ClientHeight, null, null, $"127.0.0.1:{Port}");
            var dediExe = Path.Combine(root, "r5apex_ds.exe");
            var clientExe = Path.Combine(root, UseDx12 ? "r5apex_dx12.exe" : "r5apex.exe");
            if (!File.Exists(dediExe)) dediExe = Path.Combine(root, "game", "r5apex_ds.exe");
            if (!File.Exists(clientExe)) clientExe = Path.Combine(root, "game", UseDx12 ? "r5apex_dx12.exe" : "r5apex.exe");

            JoinedRemote = false;
            AppendLog($"Starting dedi via Proton: {dediExe}");

            // The rendezvous has to exist before the dedi starts, because the
            // dedi's own boot is what feeds it.
            OpenHostGate();
            var dediOpts = new ProtonLauncher.ProtonRunOptions(
                ProtonDir: proton, PrefixPath: PrefixPath, ExePath: dediExe,
                Args: JoinArgs(dediArgs.ToArray()), WorkingDirectory: root,
                ExtraEnv: rconEnv);
            var (dedi, dediTap) = await StartGameConsoleAsync(dediOpts, LaunchRole.Dedicated).ConfigureAwait(true);
            AttachConsole(LaunchRole.Dedicated, dediTap);   // attaching starts the tap reading
            AppendLog($"Dedi pid {dedi.Id}.");

            // A hosted match is running now, so the watchdog may watch it
            // (Windows' ArmHostedMatch, which it calls at exactly this point: a
            // dedi that failed to spawn never arms anything). The heartbeat
            // itself stays quiet until the host reports a live level.
            ArmHostedMatch();

            // Deliberately not awaited: the dedi needs its head start and PLAY
            // must return to the UI immediately.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Windows waits on the host's readiness event; here the same
                    // rendezvous is the dedi's own tagged console line, with the
                    // 75-second escape hatch for a host that never says it.
                    var ready = await WaitHostReadyAsync(dedi, CancellationToken.None).ConfigureAwait(true);
                    AppendLog(ready
                        ? "Host reported a live level; starting the client."
                        : "Starting the client without a ready host.");

                    // The local client needs the same identity as a joined one: an
                    // online local match still authenticates against the master
                    // server. Offline mode needs nothing and is not gated.
                    if (!await EnsureEaChannelForOnlineAsync("Local client launch"))
                    {
                        AppendLog("The host stays up; fix the EA App and press PLAY again.");
                        return;
                    }

                    var clientOpts = new ProtonLauncher.ProtonRunOptions(
                        ProtonDir: proton, PrefixPath: PrefixPath, ExePath: clientExe,
                        Args: JoinArgs(clientArgs.ToArray()), WorkingDirectory: root,
                        ExtraEnv: rconEnv);
                    var (client, clientTap) = await StartGameConsoleAsync(clientOpts, LaunchRole.Client).ConfigureAwait(true);
                    AttachConsole(LaunchRole.Client, clientTap);
                    AppendLog($"Client pid {client.Id} launched.");
                }
                catch (Exception ex) { AppendLog("Client launch failed: " + ex.Message); }
            });
        }
        catch (Exception ex)
        {
            // A launch that did not get as far as a dedi has no host to steer.
            DropLocalRcon();
            AppendLog("Play failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task Join()
    {
        try
        {
            Save();
            if (!RequireProton()) { AppendLog("Pick a Proton build first."); return; }
            if (string.IsNullOrWhiteSpace(JoinTarget)) { AppendLog("Enter a server ip:port."); return; }
            await JoinServerAsync(JoinTarget, JoinPassword);
        }
        catch (Exception ex) { AppendLog("Join failed: " + ex.Message); }
    }

    /// <summary>Join from a row or the address box. Fire-and-forget by design: the
    /// gate inside can wait up to 45 s for EA to open its port, and the UI must not
    /// block on that. Everything it can throw is caught inside.</summary>
    public void JoinServer(string target, string? password = null)
        => _ = JoinServerAsync(target, password);

    /// <summary>
    /// Join a server, with the EA channel checked first and the client's console
    /// tapped.
    ///
    /// The tap is the port's own fix: this used the untapped <c>Start</c>, so a join
    /// produced no console lines at all — which is exactly where the identity
    /// verdict matters most, since a join is when the handshake happens. With the
    /// tap attached, <see cref="NoteClientLine"/> sees the client's own words.
    /// </summary>
    public async Task JoinServerAsync(string target, string? password = null)
    {
        try
        {
            if (!RequireProton()) { AppendLog("Pick a Proton build first."); return; }
            if (string.IsNullOrWhiteSpace(target)) { AppendLog("Enter a server ip:port."); return; }
            if (!await EnsureEaChannelForOnlineAsync($"Join {target}")) return;

            var root = InstallPath;
            var proton = EffectiveProtonDir();
            var joinDev = JoinWithoutDev || Dev;
            var clientArgs = LaunchArgBuilder.BuildClient(joinDev, OfflineNoAuth, true, "english", ClientArgs, ClientWindowMode,
                ClientWidth, ClientHeight, null, password, target);
            var clientExe = Path.Combine(root, UseDx12 ? "r5apex_dx12.exe" : "r5apex.exe");
            if (!File.Exists(clientExe)) clientExe = Path.Combine(root, "game", UseDx12 ? "r5apex_dx12.exe" : "r5apex.exe");
            JoinedRemote = true;
            AppendLog($"Joining {target} via Proton…");
            var opts = new ProtonLauncher.ProtonRunOptions(
                ProtonDir: proton, PrefixPath: PrefixPath, ExePath: clientExe,
                Args: JoinArgs(clientArgs.ToArray()), WorkingDirectory: root);
            var (c, tap) = await StartGameConsoleAsync(opts, LaunchRole.Client).ConfigureAwait(true);
            AttachConsole(LaunchRole.Client, tap);
            AppendLog($"Client pid {c.Id}. EA sign-in happens in-game.");
        }
        catch (Exception ex) { AppendLog("Join failed: " + ex.Message); }
    }

    /// <summary>Bring the EA App up through Proton, in the game's prefix.</summary>
    [RelayCommand]
    private async Task LaunchEa()
    {
        try
        {
            var exe = ProtonLauncher.FindEaDesktopExe(EaPrefixFor());
            if (exe is null)
            {
                AppendLog("EA App not found in the game prefix.");
                CheckEaFirstRun();
                return;
            }

            if (!RequireProton()) { AppendLog("Pick a Proton build first."); return; }

            var plan = EaPlan(exe, workingDirectory: Path.GetDirectoryName(exe));
            var where = ProtonLauncher.Describe(plan);
            // Held, not discarded: the EA App outlives this call by design, and its
            // readers are what keep its own lines arriving in launcher.log while it runs.
            _eaAppRun = await LaunchWindowsAsync(plan, "EA App",
                $"EA App running via {where}. The game reaches it over the host's loopback " +
                $"(port {EaChannelProbe.LsxPort}), so keep it signed in while you play.");
        }
        catch (Exception ex) { AppendLog("EA launch failed: " + ex.Message); }
    }

    /// <summary>
    /// The redistributables the EA App wants and Wine does not ship: the Microsoft
    /// core fonts, the VC++ runtimes, a Windows 10 version stamp. They go into the
    /// game's own Proton prefix, prepared through the Wine inside the Proton build
    /// (<see cref="EaPrefixPrep"/>).
    ///
    /// <para>This used to refuse in shared mode ("the EA App is running in the game's
    /// prefix, so there is nothing to prepare"), which was wrong twice over: Proton is
    /// the default runtime, so the mode that needed this most was the one that would
    /// not do it.</para>
    ///
    /// <para>Best effort, and never a reason to stop an install: a failure is reported
    /// in full, because the lines are the diagnosis.</para>
    /// </summary>
    [RelayCommand]
    private async Task PrepareEaPrefix()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            if (!RequireProton())
            {
                AppendLog("Pick a Proton build first: the components have to go in through the same "
                          + "Wine the game runs on, and that is the Wine inside the Proton build.");
                return;
            }

            await PrepEaComponentsAsync("from the Prepare button");
        }
        catch (Exception ex) { AppendLog("Preparing the EA prefix failed: " + ex.Message); }
        finally
        {
            Busy = false;
            Downloading = false;
            DownloadFraction = 0;
            DownloadStatus = "";
        }
    }

    /// <summary>
    /// The EA App's redistributables, run as part of a press rather than as a step
    /// the player has to know about. <paramref name="why"/> ends up in the log line
    /// when it does not work, because "before the installer" and "after this attempt"
    /// are different failures with different next steps.
    ///
    /// <para>Best effort on purpose. The installer and the EA App may both be fine
    /// without any of this — Proton ships fonts and the VC++ builtins — so a refusal
    /// is logged and the install carries on. What is not best effort is the guard
    /// inside: a prefix something is running in is never written into.</para>
    /// </summary>
    async Task PrepEaComponentsAsync(string why)
    {
        DownloadStatus = "preparing the EA prefix (winetricks)…";
        try
        {
            var outcome = await EaPrefixPrep.RunAsync(EaPrefixFor(), EffectiveProtonDir(), AppendLog);
            if (outcome.Ok)
                AppendLog($"EA prefix prepared {why}: {outcome.Summary}");
            else
                AppendLog($"The EA prefix was not prepared {why}. {outcome.Summary}");
        }
        catch (Exception ex) { AppendLog("Preparing the EA prefix failed: " + ex.Message); }
        finally { DownloadStatus = ""; }
    }

    [RelayCommand]
    private async Task InstallEa()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Save();
            Downloading = true;

            // 1. The runtime the EA App will be installed into: Proton, whatever is
            //    available, with the GE download as the fallback.
            DownloadStatus = "checking for a Proton build…";
            var protons = await ProtonManager.EnsureProtonAsync(AppendLog, DownloadProgressSink(),
                allowDownload: AutoDownloadProtonGe);
            Protons = new ObservableCollection<ProtonEntry>(protons);
            SelectedProton ??= ProtonDiscovery.PickLatest(protons);
            if (SelectedProton is not null) ProtonDir = SelectedProton.Dir;
            if (!RequireProton())
            {
                AppendLog(AutoDownloadProtonGe
                    ? "No Proton available even after the GE fallback."
                    : "No Proton build found. Press Download Proton-GE in Settings to fetch it.");
                return;
            }
            ProtonLauncher.EnsurePrefixDirs(PrefixPath);

            // 2. The components the EA App expects, in the prefix it will read them
            //    from. Before the installer, because the point of them is that the
            //    installer and the EA App find the fonts and runtimes Windows would
            //    have had. On a brand-new install there is no prefix yet — Proton
            //    creates it on that installer run — and the prep says so rather than
            //    bootstrapping one behind Proton's back.
            await PrepEaComponentsAsync("before the installer");

            // 3. EA installer automatically off the official website.
            DownloadStatus = "checking the EA installer…";
            var installer = await EaInstaller.EnsureDownloadedAsync(AppendLog, DownloadProgressSink());

            // 4. Set it up through Proton, in the game's prefix.
            var plan = EaPlan(installer, workingDirectory: Path.GetDirectoryName(installer));
            var eaPrefix = EaPrefixFor();
            var eaLogBefore = ProtonLauncher.FindNewestEaLog(eaPrefix);
            AppendLog($"Installing EA App from {installer} via {ProtonLauncher.Describe(plan)}…");
            var run = await LaunchWindowsAsync(plan,
                "EA installer",
                "EA installer running. Complete setup in the EA window, then press Refresh.");
            UpdateEaStatus();
            if (ProtonLauncher.IsEaInstalled(eaPrefix))
            {
                AppendLog("EA App is installed in the prefix.");
                return;
            }

            // 5. Watch the attempt. The grace window above answers "did it
            // start" and cannot answer "did it do anything", which is exactly
            // the gap that made a silently dying Burn bundle look like a
            // launcher that was still installing, forever. The run is handed
            // over rather than a boolean, because the watcher's first job is to
            // know whether the installer is still alive, and the second is to
            // keep its output coming while it is.
            Downloading = false;
            DownloadStatus = "";
            await WatchEaInstallAsync(run, eaLogBefore);

            // 6. One more attempt at the runtimes, now that the prefix exists. On a
            //    fresh shared install there was nothing to prep before step 2 —
            //    Proton builds the prefix on the installer run itself — and the
            //    attempt that just ended is usually the one that needed them. The
            //    prep refuses while anything holds the prefix open, so this cannot
            //    write under a live Wine server, whatever the watcher found.
            await PrepEaComponentsAsync("after this attempt");
        }
        catch (Exception ex) { AppendLog("EA install failed: " + ex.Message); }
        finally
        {
            Busy = false;
            Downloading = false;
            DownloadFraction = 0;
            DownloadStatus = "";
        }
    }

    /// <summary>
    /// Watch an EA install attempt and report what it actually did — the port's
    /// own addition, because the Windows launcher hands the installer over and
    /// never looks again.
    ///
    /// Three facts are polled: whether EA Desktop has appeared (success), whether
    /// the attempt's log is still growing, and whether the installer is *alive*.
    /// That third one was missing until 2026-09-22, and its absence wrote a false
    /// report: a Burn bundle downloading 234 MB says nothing for five minutes, so
    /// "the log has not grown" was read as "it exited" while the installer was
    /// alive and working, and the watcher stopped — which is how the stderr of the
    /// real failure went unwritten.
    ///
    /// <paramref name="logBefore"/> is the newest log that existed before the
    /// installer started, so a log left over from an earlier attempt is not
    /// mistaken for this one's work. The rules live in <see cref="EaInstallWatch"/>;
    /// this is only the loop, the phase narration and the reporting.
    ///
    /// Bounded on purpose: a slow installer is not an error, and after
    /// <see cref="EaInstallWatch.StopAfter"/> the player gets the prefix back
    /// whether or not it finished. What is not bounded is the capture — the child's
    /// output keeps landing in launcher.log until it exits, wherever that is.
    /// </summary>
    async Task WatchEaInstallAsync(WindowsRun run, string? logBefore)
    {
        LogOnly("Watching what the EA installer does (EA Desktop, its own log, and the process)…");
        SimpleStatus = Loc.Get("status_ea_watching");

        var started = DateTime.UtcNow;
        var baseline = EaInstallWatch.BytesOf(logBefore);
        var log = logBefore;
        var bytes = baseline;
        var lastGrowth = started;
        var said = EaInstallPhase.Unknown;
        // The prefix the attempt is happening in — the game's, which is the only one.
        // The watch itself classifies a prefix; it just has to be told which one.
        var eaPrefix = EaPrefixFor();

        while (true)
        {
            await Task.Delay(EaInstallWatch.PollEvery);
            var now = DateTime.UtcNow;

            var newest = ProtonLauncher.FindNewestEaLog(eaPrefix);
            if (!string.Equals(newest, log, StringComparison.Ordinal))
            {
                // A different file: this attempt started writing one of its own.
                log = newest;
                bytes = 0;
                lastGrowth = now;
                said = EaInstallPhase.Unknown;
            }
            var size = EaInstallWatch.BytesOf(log);
            if (size > bytes)
            {
                bytes = size;
                lastGrowth = now;
            }

            // Name the phase once per change. This is the part that keeps a healthy
            // download from looking like a hang: Burn writes nothing for minutes,
            // and "it is downloading 234 MB" is a different sentence from silence.
            var text = EaInstallWatch.TailText(log);
            var phase = EaInstallWatch.PhaseOf(text);
            if (phase != said)
            {
                said = phase;
                if (EaInstallWatch.PhaseLine(phase, EaInstallWatch.PackageBytes(text)) is { } line)
                    AppendLog(line);
            }

            // "A log exists" means a log belonging to this attempt: the one that
            // was already there only counts once it has grown.
            var fresh = log is not null
                && (!string.Equals(log, logBefore, StringComparison.Ordinal) || bytes > baseline);

            var alive = run.IsRunning;
            var outcome = EaInstallWatch.Classify(ProtonLauncher.IsEaInstalled(eaPrefix),
                fresh, now - started, alive);
            if (!EaInstallWatch.IsFinal(outcome))
                continue;

            ReportEaInstall(outcome, log, run.Tail(WindowsRun.CaptureLimit));
            return;
        }
    }

    /// <summary>Say how an EA attempt ended, and — for the endings that leave
    /// something to read — quote it, because a Burn bundle that dies leaves no
    /// other account of how far it got.
    ///
    /// Four accounts are quoted when there are four, in the order they help: the
    /// bundle's own error lines, the <c>err:</c> lines from Wine itself (which is
    /// where the failing MSI action is named), the MSI log's size with the sentence
    /// it earns, and the child's last words from stdout/stderr — the last of which
    /// exists at all only because the launch handle keeps the readers open for the
    /// whole attempt. An empty MSI log is quoted for its size, but a size is not a
    /// diagnosis: on 2026-09-22 this reported "the engine never opened the package"
    /// from a zero-byte log, and the next attempt's captured stderr showed the engine
    /// had run and had halted at EA's own custom action.
    ///
    /// <paramref name="captured"/> is read whole and printed in part: the classifier
    /// and the MSI note see every line the launch kept, and the log shows the last
    /// <see cref="EaInstallWatch.QuotableLines"/> of them, because a report that
    /// prints two hundred lines of Wine fixmes is a report nobody reads.</summary>
    void ReportEaInstall(EaInstallOutcome outcome, string? log, IReadOnlyList<string>? captured)
    {
        switch (outcome)
        {
            case EaInstallOutcome.DesktopInstalled:
                AppendLog("EA App is installed in the prefix.");
                UpdateEaStatus();
                return;
            case EaInstallOutcome.LogStalled:
                AppendLog("The EA installer has exited and no EA App appeared. Its own log says "
                          + "how far it got:");
                break;
            case EaInstallOutcome.NoTrace:
                AppendLog("The EA installer wrote nothing at all in the prefix, so it never reached "
                          + "its own first log line. That is usually Wine refusing the bundle, not "
                          + "the bundle refusing to run.");
                break;
            case EaInstallOutcome.StillWorking:
                SimpleStatus = Loc.Get("status_ea_slow");
                AppendLog($"The EA installer is still running after "
                          + $"{(int)EaInstallWatch.StopAfter.TotalMinutes} minutes. Finish it in the "
                          + "EA window, then press Refresh.");
                return;
        }

        // The bundle's log is the whole history; the MSI's note is the verdict, and
        // the verdict needs the child's own output to be worth anything: Wine's MSI
        // engine is the part that fails, and it says why in stderr. The bundle's log
        // specifically, not the newest log in the prefix — EA Desktop's own log is
        // newer than the bundle's whenever the App is running, and it never names
        // WixBundleLog.
        var history = EaInstallWatch.TailText(
            ProtonLauncher.FindNewestBundleLog(EaPrefixFor()), 32768);
        foreach (var line in EaInstallWatch.FailureLines(history))
            LogOnly(line);

        var wineText = captured is { Count: > 0 } ? string.Join('\n', captured) : null;
        if (EaInstallWatch.WineDiagnosticLines(wineText) is { Count: > 0 } wineLines)
        {
            LogOnly($"--- Wine's own errors ({wineLines.Count}) ---");
            foreach (var line in wineLines)
                LogOnly(line);
        }

        if (EaInstallWatch.MsiLogPath(history) is { } msiPath)
        {
            var host = ToHostPath(msiPath);
            var bytes = EaInstallWatch.BytesOf(host);
            LogOnly($"--- {Path.GetFileName(msiPath)}: {bytes} bytes ---");
            if (EaInstallWatch.MsiFailureNote(history, bytes, wineText) is { } note)
                AppendLog(note);
        }

        ReportEaLog(ProtonLauncher.FindNewestEaLog(EaPrefixFor()));

        var quoted = EaInstallWatch.QuotableLines(captured);
        if (quoted.Count > 0)
        {
            LogOnly($"--- the installer's own output: last {quoted.Count} of {captured!.Count} lines ---");
            foreach (var line in quoted)
                LogOnly(line);
        }
        else
        {
            LogOnly("--- the installer's own output: nothing was captured ---");
        }
    }

    /// <summary>A <c>C:\…</c> path from a Wine log, as this machine sees it. The
    /// prefix's drive_c is the whole of C:, whichever runtime owns the prefix.</summary>
    string ToHostPath(string windowsPath)
    {
        var driveC = PrefixLayout.DriveCFor(EaPrefixFor());
        var rest = windowsPath.Length > 2 && windowsPath[1] == ':'
            ? windowsPath[2..].TrimStart('\\', '/')
            : windowsPath.TrimStart('\\', '/');
        return Path.Combine(driveC, rest.Replace('\\', '/'));
    }

    /// <summary>Quote the tail of the newest EA log in the prefix and name the
    /// file, so the whole of it can be read afterwards.</summary>
    void ReportEaLog(string? log)
    {
        if (log is null)
        {
            AppendLog("No EA log in the prefix to read.");
            return;
        }
        var tail = ProtonLauncher.Tail(log, 12);
        if (tail.Count > 0)
        {
            LogOnly($"--- {Path.GetFileName(log)}: last {tail.Count} lines ---");
            foreach (var line in tail)
                LogOnly(line);
        }
        AppendLog("EA log: " + log);
    }

    /// <summary>
    /// Write the launcher's menu entries: one for itself, and one for the EA App
    /// once the prefix has EA Desktop in it.
    ///
    /// Windows gets these from its setup program; on Linux there is no setup
    /// program for a hand-built launcher, so the launcher writes its own. The
    /// binary that writes the entry is the one the entry points at
    /// (<see cref="Environment.ProcessPath"/>), so a shortcut made from a copy
    /// in <c>~/.local/opt</c> points at that copy and not at a build tree.
    ///
    /// Pointing at the EA App is the "launch alongside" half: sign-in happens in
    /// EA's own client, in the same prefix, and a menu entry saves reaching for a
    /// terminal every time. It is skipped, loudly, when there is nothing to point
    /// at — an entry that does nothing is worse than no entry.
    /// </summary>
    [RelayCommand]
    private void CreateShortcuts()
    {
        try
        {
            var exec = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exec))
            {
                AppendLog(Loc.Get("shortcuts_no_exe"));
                return;
            }

            // The EA entry runs Proton directly: the prefix is the game's, so there is
            // nothing for a menu entry to build first, and its exec is the same
            // environment the launcher itself sets.
            var eaPlan = new EaAppShortcut(EaPrefixFor(),
                ProtonLauncher.FindEaDesktopExe(EaPrefixFor()) ?? "", EffectiveProtonDir());
            var results = DesktopShortcuts.WriteAll(exec, EffectiveProtonDir(), PrefixPath,
                AppIcon.Source(), ea: eaPlan);
            foreach (var r in results)
            {
                LogOnly(r.Written
                    ? $"Shortcut written: {r.Path}"
                    : $"Shortcut skipped ({r.Name}): {r.Problem}");
            }

            var made = results.Where(r => r.Written).Select(r => r.Name).ToList();
            var skipped = results.Where(r => !r.Written).Select(r => r.Name).ToList();

            // Only worth asking when something was actually written.
            var caches = made.Count > 0 ? DesktopShortcuts.RefreshCaches() : Array.Empty<string>();
            if (caches.Count > 0)
                LogOnly("Refreshed: " + string.Join(", ", caches));

            // One line, through AppendLog, so the reason a piece is missing
            // reaches the status bar and the log file rather than only the pane.
            AppendLog(skipped.Count == 0
                ? Loc.Format("shortcuts_created", string.Join(", ", made))
                : Loc.Format("shortcuts_partial", string.Join(", ", skipped)));
        }
        catch (Exception ex) { AppendLog("Shortcuts failed: " + ex.Message); }
    }
}