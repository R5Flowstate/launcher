using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    private readonly List<ServerRowViewModel> _serverRows = new();
    private DispatcherTimer? _browserTimer;
    private CancellationTokenSource? _browserCts;
    private enum SimpleTab { Play, Servers, Leaderboards, Console, Mods, Blog, Notes, Credits, Settings }

    private SimpleTab _simpleTab;
    private bool _joinBusy;
    private ServerListing? _joinedServer;
    private bool _steerBusy;
    private DispatcherTimer? _steerCooldown;
    private bool _eulaDialogOpen;
    private int _eulaVersionCurrent;

    private void InitServerBrowser()
    {
        _browserTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _browserTimer.Tick += async (_, _) =>
        {
            if (IsBrowserVisible())
                await RefreshServerListAsync(quiet: true).ConfigureAwait(true);
        };
        ApplySimpleTab(SimpleTab.Play);
        if (!_settings.SimpleMode)
            _ = OpenServersAsync();
        RefreshDediModPolicyUi();
    }

    private bool IsBrowserVisible()
    {
        if (!IsLoaded)
            return false;
        if (_settings.SimpleMode)
        {
            return _simpleTab == SimpleTab.Servers &&
                   !NeedsSetup(ReadInstallPathBox()) &&
                   ServersUnlocked();
        }
        return true;
    }

    private void OnSimpleTabLocal(object sender, RoutedEventArgs e) =>
        ApplySimpleTab(SimpleTab.Play);

    private void OnSimpleTabServers(object sender, RoutedEventArgs e) =>
        _ = OpenServersAsync();

    private void OnSimpleTabMods(object sender, RoutedEventArgs e)
    {
        if (TabBlockedBySetup())
            return;

        ApplySimpleTab(SimpleTab.Mods);
        _ = RefreshModsPanelAsync();
    }

    private void OnSimpleTabNotes(object sender, RoutedEventArgs e) =>
        ApplySimpleTab(SimpleTab.Notes);

    private void OnSimpleTabCredits(object sender, RoutedEventArgs e) =>
        ApplySimpleTab(SimpleTab.Credits);

    private const double TabScrollStep = 140;

    private void OnTabScrollLeft(object sender, RoutedEventArgs e) =>
        TabScroll.ScrollToHorizontalOffset(Math.Max(0, TabScroll.HorizontalOffset - TabScrollStep));

    private void OnTabScrollRight(object sender, RoutedEventArgs e) =>
        TabScroll.ScrollToHorizontalOffset(
            Math.Min(TabScroll.ScrollableWidth, TabScroll.HorizontalOffset + TabScrollStep));

    private void OnTabScrollWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (TabScroll.ScrollableWidth <= 0)
            return;
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnTabScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var overflow = TabScroll.ScrollableWidth > 0.5;
        BtnTabScrollLeft.Visibility =
            overflow && TabScroll.HorizontalOffset > 0.5
                ? Visibility.Visible : Visibility.Collapsed;
        BtnTabScrollRight.Visibility =
            overflow && TabScroll.HorizontalOffset < TabScroll.ScrollableWidth - 0.5
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSimpleTabSettings(object sender, RoutedEventArgs e)
    {
        if (TabBlockedBySetup())
            return;

        if (!_settings.SimpleMode && _simpleTab == SimpleTab.Settings)
        {
            ApplySimpleTab(SimpleTab.Play);
            return;
        }

        ApplySimpleTab(SimpleTab.Settings);
    }

    private async Task OpenServersAsync()
    {
        if (_eulaDialogOpen)
            return;

        if (_settings.SimpleMode && NeedsSetup(ReadInstallPathBox()))
        {
            ApplySimpleTab(SimpleTab.Play);
            SetSimpleStatus(Loc.Get("status_install_first"));
            return;
        }

        if (!await EnsureEulaAcceptedAsync().ConfigureAwait(true))
        {
            if (_settings.SimpleMode)
            {
                ApplySimpleTab(SimpleTab.Play);
                SetSimpleStatus(Loc.Get("status_eula_servers"));
            }
            else
            {
                SetBrowserStatus(Loc.Get("status_eula_servers_join"));
                ClearServerList();
            }
            return;
        }

        ApplySimpleTab(SimpleTab.Servers);
        await RefreshServerListAsync(quiet: false).ConfigureAwait(true);
    }

    private void ApplySimpleTab(SimpleTab tab)
    {
        _simpleTab = tab;
        if (PanelSimpleLocal is not null)
            PanelSimpleLocal.Visibility = tab == SimpleTab.Play ? Visibility.Visible : Visibility.Collapsed;
        if (PanelSimpleServers is not null)
            PanelSimpleServers.Visibility = tab == SimpleTab.Servers ? Visibility.Visible : Visibility.Collapsed;
        SyncLeaderboardTab(tab);
        if (PanelSimpleConsole is not null)
            PanelSimpleConsole.Visibility = tab == SimpleTab.Console ? Visibility.Visible : Visibility.Collapsed;
        if (PanelSimpleMods is not null)
            PanelSimpleMods.Visibility = tab == SimpleTab.Mods ? Visibility.Visible : Visibility.Collapsed;
        SyncBlogTab(tab);
        if (PanelSimpleNotes is not null)
            PanelSimpleNotes.Visibility = tab == SimpleTab.Notes ? Visibility.Visible : Visibility.Collapsed;
        if (tab == SimpleTab.Notes && ScrollNotes is not null)
            Dispatcher.BeginInvoke(ScrollNotes.ScrollToTop, DispatcherPriority.Background);
        if (PanelSimpleCredits is not null)
            PanelSimpleCredits.Visibility = tab == SimpleTab.Credits ? Visibility.Visible : Visibility.Collapsed;
        if (PanelSimpleSettings is not null)
            PanelSimpleSettings.Visibility = tab == SimpleTab.Settings ? Visibility.Visible : Visibility.Collapsed;
        if (TxtBrowserStatus is not null)
            TxtBrowserStatus.Visibility = tab == SimpleTab.Servers ? Visibility.Visible : Visibility.Collapsed;
        if (BtnBrowserRefresh is not null)
        {
            var showRefresh = tab is SimpleTab.Servers or SimpleTab.Leaderboards or SimpleTab.Blog;
            BtnBrowserRefresh.Visibility = showRefresh ? Visibility.Visible : Visibility.Collapsed;
            BtnBrowserRefresh.ToolTip = tab switch
            {
                SimpleTab.Leaderboards => Loc.Get("tip_refresh_lb"),
                SimpleTab.Blog => Loc.Get("tip_refresh_blog"),
                _ => Loc.Get("tip_refresh_servers"),
            };
        }
        if (BtnConsoleClear is not null)
            BtnConsoleClear.Visibility = tab == SimpleTab.Console ? Visibility.Visible : Visibility.Collapsed;
        if (BtnConsoleFind is not null)
            BtnConsoleFind.Visibility = tab == SimpleTab.Console ? Visibility.Visible : Visibility.Collapsed;
        RefreshConsoleChrome();
        // Both bars own a smart button, so the caption/style has to be re-derived
        // for whichever one just became visible.
        if (IsLoaded)
            RefreshSimplePlayButton();

        StyleTab(BtnTabLocal, tab == SimpleTab.Play);
        StyleTab(BtnTabServers, tab == SimpleTab.Servers);
        StyleTab(BtnTabConsole, tab == SimpleTab.Console);
        StyleTab(BtnTabMods, tab == SimpleTab.Mods);
        StyleTab(BtnTabNotes, tab == SimpleTab.Notes);
        StyleTab(BtnTabCredits, tab == SimpleTab.Credits);
        SyncNotesUnreadDot(markSeen: tab == SimpleTab.Notes);
        StyleHeaderTool(BtnHeaderConsole, tab == SimpleTab.Console);
        StyleHeaderTool(BtnTabSettings, tab == SimpleTab.Settings);
        StyleHeaderTool(BtnTabToolSettings, tab == SimpleTab.Settings);

        if (!_settings.SimpleMode)
        {
            var overlay = tab is SimpleTab.Console or SimpleTab.Settings;
            if (PanelAdvanced is not null)
                PanelAdvanced.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
            if (PanelSimple is not null)
                PanelSimple.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
            if (BarSimpleTabs is not null)
                BarSimpleTabs.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
            if (BarSimpleFooter is not null)
                BarSimpleFooter.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            if (BarSimpleTabs is not null)
                BarSimpleTabs.Visibility = Visibility.Visible;
            if (BarSimpleFooter is not null)
                BarSimpleFooter.Visibility = Visibility.Visible;
        }

        if (tab == SimpleTab.Credits)
            BindCreditsShuffled();
        if (tab == SimpleTab.Console)
            PinFollowedConsoles();

        RefreshHeaderSubtitle();

        if (tab == SimpleTab.Servers && ServersUnlocked() && !NeedsSetup(ReadInstallPathBox()))
        {
            if (_browserTimer is not null && !_browserTimer.IsEnabled)
                _browserTimer.Start();
        }
        else
        {
            _browserTimer?.Stop();
        }

        RefreshServersLockedChrome();
        RefreshSimplePlayButton();
    }

    void BindCreditsShuffled()
    {
        if (ListCredits is null)
            return;

        var src = CreditsCatalog.All;
        var n = src.Count;
        var copy = new CreditEntry[n];
        for (var i = 0; i < n; i++)
            copy[i] = src[i];
        for (var i = n - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        ListCredits.ItemsSource = copy;
    }

    private static void StyleTab(Button? btn, bool on)
    {
        if (btn is null)
            return;
        var key = on ? "TabButtonOn" : "TabButton";
        if (btn.TryFindResource(key) is Style style)
            btn.Style = style;
    }

    private static void StyleHeaderTool(Button? btn, bool on)
    {
        if (btn is null)
            return;
        btn.SetResourceReference(Control.ForegroundProperty, on ? "AccentBright" : "TextSecondary");
    }

    private void OnBrowserRefresh(object sender, RoutedEventArgs e)
    {
        if (_simpleTab == SimpleTab.Leaderboards)
            OnBrowserRefreshFromLeaderboards();
        else if (_simpleTab == SimpleTab.Blog)
            _ = RefreshBlogAsync();
        else
            _ = RefreshServerListAsync(quiet: false);
    }

    private bool ServersUnlocked()
    {
        if (_eulaVersionCurrent <= 0)
            return _settings.EulaVersionAccepted > 0;
        return _settings.EulaVersionAccepted >= _eulaVersionCurrent;
    }

    private void SaveEulaAccepted(int version)
    {
        _settings.EulaVersionAccepted = version;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
    }

    private async Task<bool> EnsureEulaAcceptedAsync()
    {
        // Already accepted at this session's known-current version: no round trip.
        // A server-side bump is picked up on the next launcher start.
        if (_eulaVersionCurrent > 0 && _settings.EulaVersionAccepted >= _eulaVersionCurrent)
            return true;

        var lang = NoticeLanguages.ResolveUi(_settings.UiLanguage, _settings.EulaLanguage);
        var fetched = await MasterServerClient.GetEulaAsync(CurrentMasterServerUrl(), lang)
            .ConfigureAwait(true);
        if (!fetched.Success)
        {
            SetBrowserStatus(fetched.Error ?? Loc.Get("status_eula_failed"));
            if (_settings.SimpleMode)
                SetSimpleStatus(Loc.Get("status_eula_failed_retry"));
            return false;
        }

        _eulaVersionCurrent = fetched.Version > 0 ? fetched.Version : 1;
        if (_settings.EulaVersionAccepted >= _eulaVersionCurrent)
            return true;

        _eulaDialogOpen = true;
        try
        {
            var dlg = new EulaWindow(
                this,
                CurrentMasterServerUrl(),
                requireAccept: true,
                prefetched: fetched,
                settings: _settings);
            var ok = dlg.ShowDialog() == true && dlg.Accepted;
            if (!ok)
                return false;
            SaveEulaAccepted(dlg.AcceptedVersion);
            _eulaVersionCurrent = dlg.AcceptedVersion;
            return true;
        }
        finally
        {
            _eulaDialogOpen = false;
        }
    }

    private void ClearServerList()
    {
        _serverRows.Clear();
        BindServerLists();
    }

    private void RefreshServersLockedChrome()
    {
        var missing = NeedsSetup(ReadInstallPathBox());
        var locked = missing || !ServersUnlocked();

        if (TxtServersLocked is not null)
        {
            if (missing)
            {
                TxtServersLocked.Text = string.Empty;
                TxtServersLocked.Visibility = Visibility.Collapsed;
            }
            else if (!ServersUnlocked())
            {
                TxtServersLocked.Text = Loc.Get("status_eula_servers");
                TxtServersLocked.Visibility = Visibility.Visible;
            }
            else
            {
                TxtServersLocked.Text = string.Empty;
                TxtServersLocked.Visibility = Visibility.Collapsed;
            }
        }

        if (ScrollServersSimple is not null)
            ScrollServersSimple.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (BarServersFooter is not null)
            BarServersFooter.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (locked && PanelServersClientOptions is not null)
            PanelServersClientOptions.Visibility = Visibility.Collapsed;
        if (ListServersSimple is not null && locked)
            ClearServerList();
    }

    private async Task RefreshServerListAsync(bool quiet)
    {
        if (NeedsSetup(ReadInstallPathBox()) || !ServersUnlocked())
        {
            ClearServerList();
            RefreshServersLockedChrome();
            return;
        }

        _browserCts?.Cancel();
        _browserCts = new CancellationTokenSource();
        var ct = _browserCts.Token;

        if (!quiet)
            SetBrowserStatus(Loc.Get("lb_refreshing"));

        var wire = VersionIdentity.ResolveExpectedWireVersion(
            TxtInstallRoot?.Text?.Trim() ?? string.Empty,
            _manifest?.EffectiveGateName);
        var url = CurrentMasterServerUrl();

        ServerListResult result;
        try
        {
            result = await MasterServerClient.ListServersAsync(url, wire, Loc.Code, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _serverRows.Clear();
        if (result.Success)
        {
            foreach (var row in result.Servers)
                _serverRows.Add(ToRow(row));
        }

        BindServerLists();

        if (!result.Success)
        {
            SetBrowserStatus(result.Error ?? Loc.Get("browser_failed"));
            return;
        }

        if (result.UpdateRequired)
        {
            SetBrowserStatus(Loc.Get("browser_update_required"));
            return;
        }

        PaintBrowserCount();
    }

    private void RelabelServerRows()
    {
        if (_serverRows.Count == 0)
            return;
        var kept = _serverRows.Select(r =>
        {
            var n = ToRow(r.Listing);
            n.PingMs = r.PingMs;
            n.IsFavorite = r.IsFavorite;
            n.IsSteering = r.IsSteering;
            return n;
        }).ToList();
        _serverRows.Clear();
        _serverRows.AddRange(kept);
        ApplyFavorites();
        SortServerRows();
        if (ListServersSimple is not null)
        {
            ListServersSimple.ItemsSource = null;
            ListServersSimple.ItemsSource = _serverRows;
        }
        if (ListServersAdvanced is not null)
        {
            ListServersAdvanced.ItemsSource = null;
            ListServersAdvanced.ItemsSource = _serverRows;
        }
        PaintBrowserCount();
    }

    private void PaintBrowserCount()
    {
        var n = _serverRows.Count;
        var players = 0;
        foreach (var r in _serverRows)
            players += r.Listing.NumPlayers;
        SetBrowserStatus(n == 0
            ? Loc.Get("browser_none")
            : Loc.Format("browser_count", n, players));
    }

    private ServerRowViewModel ToRow(ServerListing listing)
    {
        var mapLabel = MapOption.Label(listing.Map, _catalog.MapNames);
        var playlistLabel = listing.Playlist;
        var pl = _catalog.Find(listing.Playlist);
        if (pl is not null && !string.IsNullOrWhiteSpace(pl.Title))
            playlistLabel = pl.Title;
        return new ServerRowViewModel(listing, mapLabel, playlistLabel);
    }

    private void BindServerLists()
    {
        ApplyFavorites();
        SortServerRows();

        if (ListServersSimple is not null)
        {
            ListServersSimple.ItemsSource = null;
            ListServersSimple.ItemsSource = _serverRows;
        }
        if (ListServersAdvanced is not null)
        {
            ListServersAdvanced.ItemsSource = null;
            ListServersAdvanced.ItemsSource = _serverRows;
        }

        _ = MeasureLatencyAsync(_serverRows.ToList());
    }

    private void ApplyFavorites()
    {
        foreach (var r in _serverRows)
            r.IsFavorite = _settings.FavoriteServers.Contains(r.Key);
    }

    /// <summary>Favourites first, then the fuller servers -- an empty lobby is rarely the answer.</summary>
    private void SortServerRows()
    {
        var sorted = _serverRows
            .OrderByDescending(r => r.IsFavorite)
            .ThenByDescending(r => r.Listing.NumPlayers)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _serverRows.Clear();
        _serverRows.AddRange(sorted);
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRowViewModel row })
            return;

        if (!_settings.FavoriteServers.Remove(row.Key))
            _settings.FavoriteServers.Add(row.Key);

        row.IsFavorite = _settings.FavoriteServers.Contains(row.Key);
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }

        BindServerLists();
    }

    /// <summary>
    /// ICMP round-trip per host. It is not the game's own latency -- the server
    /// does not publish one -- but it separates a nearby host from one an ocean
    /// away, which is what the column is for. All hosts probe concurrently.
    /// </summary>
    private async Task MeasureLatencyAsync(List<ServerRowViewModel> rows)
    {
        var probes = new List<Task>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Listing.Ip) || row.Listing.IsUpdateNotice)
                continue;
            probes.Add(ProbeLatencyAsync(row));
        }

        await Task.WhenAll(probes).ConfigureAwait(true);
    }

    private static async Task ProbeLatencyAsync(ServerRowViewModel row)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(row.Listing.Ip, 1500).ConfigureAwait(true);
            row.PingMs = reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? (int)Math.Max(1, reply.RoundtripTime)
                : 0;
        }
        catch
        {
            row.PingMs = 0;
        }
    }

    private void SetBrowserStatus(string text)
    {
        if (TxtBrowserStatus is not null)
            TxtBrowserStatus.Text = text;
        if (TxtAdvBrowserStatus is not null)
            TxtAdvBrowserStatus.Text = text;
        if (_settings.SimpleMode && _simpleTab == SimpleTab.Servers)
            SetSimpleStatus(text);
    }

    /// <summary>The play host is fixed; there is nothing for a user to point elsewhere.</summary>
    private static string CurrentMasterServerUrl() => ProductConstants.DefaultMasterServerUrl;

    private void OnReadEula(object sender, RoutedEventArgs e)
    {
        if (_eulaDialogOpen)
            return;
        var dlg = new EulaWindow(
            this,
            CurrentMasterServerUrl(),
            requireAccept: false,
            settings: _settings);
        dlg.ShowDialog();
    }

    /// <summary>Runs a stashed r5flowstate://join once, resolving the key against a fresh list.</summary>
    internal void ConsumePendingJoin()
    {
        var key = JoinLink.TakePending();
        if (string.IsNullOrEmpty(key))
            return;
        _ = JoinByKeyAsync(key);
    }

    private async Task JoinByKeyAsync(string key)
    {
        ApplySimpleTab(SimpleTab.Servers);
        await RefreshServerListAsync(quiet: true).ConfigureAwait(true);
        var row = _serverRows.FirstOrDefault(r => string.Equals(r.Listing.Key, key, StringComparison.Ordinal));
        if (row is null)
        {
            SetBrowserStatus(Loc.Get("status_join_not_found"));
            return;
        }
        await RunJoinAsync(row.Listing).ConfigureAwait(true);
    }

    private void OnJoinServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ServerRowViewModel row })
            return;
        _ = RunJoinAsync(row.Listing);
    }

    private bool ConfirmOnlineJoin()
    {
        if (!IsOfflineOn())
            return true;

        var ans = MessageBox.Show(
            this,
            Loc.Get("ask_offline_join"),
            Loc.Get("join_title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (ans != MessageBoxResult.Yes)
            return false;

        ApplyOfflineNoAuth(false);
        Log("Offline cleared: joining a listed server");
        return true;
    }

    /// <summary>
    /// True when a client this launcher started is still up and still answering
    /// on its command pipe -- the only case where an in-place hop is possible.
    /// </summary>
    private bool ClientIsSteerable()
    {
        if (_consoleClient is null)
            return false;
        var root = ReadInstallPathBox();
        return !string.IsNullOrWhiteSpace(root)
            && ProcessSpawner.IsRoleAlive(LaunchRole.Client, root);
    }

    /// <summary>
    /// A listing comes off the master server, and this address is about to be
    /// written into a line the game's command buffer executes -- which splits on
    /// ';' and newlines. Anything outside a literal host and port is refused
    /// rather than escaped, so there is nothing to get the escaping wrong on.
    /// </summary>
    private static bool TryFormatConsoleTarget(string? ip, int port, out string target)
    {
        target = string.Empty;
        if (!LaunchArgs.IsSafeConnectHost(ip) || port <= 0 || port > 65535)
            return false;

        var host = ip!.Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        target = host.Contains(':')
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
        return true;
    }

    /// <summary>
    /// One steering command at a time, with a settle window after it. A connect
    /// is asynchronous inside the engine, so stacking them from a double-click
    /// is exactly the way to tear a session in half.
    /// </summary>
    private bool BeginSteer()
    {
        if (_steerBusy)
            return false;

        _steerBusy = true;
        RefreshConnectionChrome();

        _steerCooldown?.Stop();
        _steerCooldown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _steerCooldown.Tick += (_, _) =>
        {
            _steerCooldown?.Stop();
            _steerCooldown = null;
            _steerBusy = false;
            RefreshConnectionChrome();
        };
        _steerCooldown.Start();
        return true;
    }

    /// <summary>
    /// Sends the running client to another server. bridge_connect is the
    /// UI-safe entry: it quotes the address so Tokenize cannot split on the
    /// colon and drop the port. Returns false to fall back to a fresh launch.
    /// </summary>
    private bool TryHopToServer(ServerListing listing, string root, string target, string? password)
    {
        if (!ClientIsSteerable())
            return false;

        // leftover C2S tag Hidden_Game's an open host
        if (!listing.HasPassword && _joinedServer is { HasPassword: true })
            return false;

        if (!TryFormatConsoleTarget(listing.Ip, listing.Port, out var safeTarget))
        {
            Log($"Join: refusing to steer to a malformed address '{listing.Ip}:{listing.Port}'");
            SetBrowserStatus(Loc.Get("status_malformed"));
            return true;
        }

        // Swallow the click rather than falling through to a relaunch: a second
        // press during the settle window means impatience, not a new intent.
        if (!BeginSteer())
        {
            SetBrowserStatus(Loc.Get("status_switching"));
            return true;
        }

        var line = LaunchArgs.FormatBridgeConnectLine(safeTarget, password);
        if (_consoleClient?.TryWriteCommand(line) != true)
        {
            Log("Join: command channel refused the hop; relaunching instead");
            EndSteer();
            return false;
        }

        _joinedServer = listing;
        var shown = LaunchArgs.FormatBridgeConnectLine(safeTarget, string.IsNullOrEmpty(password) ? null : "****");
        AppendConsoleLine(LaunchRole.Client, "] " + shown);
        Log($"Join (in place): {listing.Name} -> {safeTarget}");
        SetBrowserStatus(Loc.Format("status_switching_to", listing.Name));
        SetSimpleStatus(Loc.Format("status_joined", listing.Name));
        RefreshConnectionChrome();
        return true;
    }

    private void EndSteer()
    {
        _steerCooldown?.Stop();
        _steerCooldown = null;
        _steerBusy = false;
        RefreshConnectionChrome();
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (!ClientIsSteerable() || _joinedServer is null)
        {
            RefreshConnectionChrome();
            return;
        }

        if (!BeginSteer())
            return;

        if (_consoleClient?.TryWriteCommand("disconnect") != true)
        {
            EndSteer();
            SetBrowserStatus(Loc.Get("status_disconnect_failed"));
            return;
        }

        AppendConsoleLine(LaunchRole.Client, "] disconnect");
        _joinedServer = null;
        Log("Disconnect sent to the running client");
        SetBrowserStatus(Loc.Get("status_disconnected_client"));
        SetSimpleStatus(Loc.Get("status_disconnected"));
        RefreshConnectionChrome();
    }

    private void RefreshConnectionChrome()
    {
        var connected = _joinedServer is not null && ClientIsSteerable();

        if (BtnDisconnect is not null)
        {
            BtnDisconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            BtnDisconnect.IsEnabled = connected && !_steerBusy;
        }

        if (TxtConnectedTo is not null)
        {
            TxtConnectedTo.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (connected)
                TxtConnectedTo.Text = _steerBusy
                    ? Loc.Get("status_switching_short")
                    : Loc.Format("status_connected_to", _joinedServer!.Name);
        }

        foreach (var row in _serverRows)
            row.IsSteering = _steerBusy;

        ApplyConsoleView();
        RefreshLiveSessionButton();
    }

    /// <summary>
    /// forceRelaunch skips the in-place hop: a renderer switch has to replace the
    /// client image, and a hop keeps the one already running.
    /// </summary>
    private async Task RunJoinAsync(ServerListing listing, bool forceRelaunch = false)
    {
        if (_joinBusy || _quickPlayBusy || _installBusy)
            return;

        if (!listing.CanJoin)
        {
            SetBrowserStatus(Loc.Get("status_not_joinable"));
            return;
        }

        var wasOffline = IsOfflineOn();
        if (!ConfirmOnlineJoin())
            return;

        var joinPw = string.Empty;
        if (listing.HasPassword)
        {
            if (!JoinPasswordWindow.TryAsk(this, listing.Name, out joinPw))
                return;
        }

        if (!ServersUnlocked())
        {
            if (!await EnsureEulaAcceptedAsync().ConfigureAwait(true))
            {
                SetBrowserStatus(Loc.Get("status_eula_join"));
                return;
            }
        }

        _joinBusy = true;
        RefreshSimplePlayButton();
        try
        {
            PersistSettingsFromUi();
            if (wasOffline)
                ApplyOfflineNoAuth(false);
            var root = TxtInstallRoot.Text.Trim();
            var (ok, reason) = await EnsurePlayContentAsync(
                requireClient: true,
                requireServer: false,
                autoRepairCorrupt: true).ConfigureAwait(true);
            if (!ok)
            {
                Log("Join refused: " + reason);
                if (_settings.SimpleMode)
                    SetSimpleStatus(PlainContentGateMessage(reason));
                else
                {
                    SetError(reason ?? "content gate");
                    MessageBox.Show(this, reason ?? Loc.Get("msg_content_not_ready"), Loc.Get("title_content"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            if (!await EnsureJoinModsAsync(listing).ConfigureAwait(true))
            {
                Log("Join cancelled: mod requirements were not met.");
                SetBrowserStatus(Loc.Get("mods_join_cancelled"));
                RestoreSessionMods(force: true);
                return;
            }

            var modsRelaunch = _sessionModsForceRelaunch;
            _sessionModsForceRelaunch = false;

            if (!LaunchArgs.IsSafeConnectHost(listing.Ip))
            {
                Log($"Join: refusing malformed host '{listing.Ip}'");
                SetBrowserStatus(Loc.Get("status_not_joinable"));
                RestoreSessionMods(force: true);
                return;
            }

            var target = LaunchArgs.FormatConnectTarget(listing.Ip, listing.Port);
            // Listing `key` is unused: S21 client packet encryption is forced off.

            // A client we still hold a command channel to can hop servers where
            // it stands; relaunching would cost the whole load again. An
            // offline-auth client cannot pass server auth, so it relaunches.
            // Session mod filters and newly installed mods only take effect on
            // a fresh boot (the engine reads mods.vdf once).
            if (!forceRelaunch && !modsRelaunch && !_lastClientOfflineAuth &&
                TryHopToServer(listing, root, target, listing.HasPassword ? joinPw : null))
                return;

            Log($"Join: {listing.Name} -> +connect {target}");
            SetBrowserStatus("Joining " + listing.Name + "…");

            DisarmHostedMatch();
            var killed = await Task.Run(() => ProcessSpawner.KillRole(LaunchRole.Client, root))
                .ConfigureAwait(true);
            if (killed > 0)
                Log($"Join: killed {killed} prior client process(es)");

            // Going online is a client-only session; a dedi tap left over from a
            // local match would only show a dead server pane.
            DropHostedConsoles();
            ResetClientConsoleForNewSession();

            await Task.Delay(300).ConfigureAwait(true);

            var clientArgs = BuildClientArgs(
                includeConnect: true,
                connectHost: listing.Ip,
                connectPort: listing.Port,
                connectPassword: listing.HasPassword ? joinPw : string.Empty,
                forceOnline: true);
            var clientResult = SpawnRoleResult(LaunchRole.Client, clientArgs, quiet: _settings.SimpleMode);
            if (!clientResult.Ok)
            {
                RestoreSessionMods(force: true);
                if (_settings.SimpleMode)
                    SetSimpleStatus(Loc.Get("status_join_failed_start"));
                else
                    SetError(clientResult.Error ?? "client spawn failed");
                return;
            }

            _lastClientPid = clientResult.ProcessId;
            _joinedServer = listing;
            RefreshConnectionChrome();
            UpdateKillButtons();
            MaybeOpenConsoleForLaunch();
            UpdateStatus($"Join OK  |  {PidLine()}  |  {target}");
            SetBrowserStatus("Joining " + listing.Name + ". Origin sign-in happens in-game.");
            if (_settings.SimpleMode)
                SetSimpleStatus(Loc.Format("status_joining", listing.Name));
        }
        catch (Exception ex)
        {
            Log("Join failed: " + ex.Message);
            if (!ProcessSpawner.IsRoleAlive(LaunchRole.Client, TxtInstallRoot.Text.Trim()))
                RestoreSessionMods(force: true);
            if (_settings.SimpleMode)
                SetSimpleStatus(Loc.Get("status_join_failed"));
            else
            {
                SetError(ex.Message);
                MessageBox.Show(this, Loc.Format("msg_join_failed", ex.Message), Loc.Get("title_app"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _joinBusy = false;
            UpdateKillButtons();
        }
    }
}
