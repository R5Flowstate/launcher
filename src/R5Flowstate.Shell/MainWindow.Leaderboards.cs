using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace R5Flowstate.Shell;

public partial class MainWindow
{
    private readonly List<LeaderboardRowViewModel> _lbRows = new();
    private DispatcherTimer? _lbTimer;
    private DispatcherTimer? _lbSearchTimer;
    private DispatcherTimer? _activityTimer;
    private CancellationTokenSource? _lbCts;
    private CancellationTokenSource? _lbDetailCts;
    private CancellationTokenSource? _activityCts;
    private LeaderboardRowViewModel? _lbSelected;
    private string _lbSort = "score";
    private string _lbOrder = "desc";
    private string _lbSeason = "current";
    private string _lbQ = "";
    private int _lbOffset;
    private bool _lbHasNext;
    private bool _lbHasPrev;
    private bool _lbSeasonSilent;
    private bool _lbViewHistory;
    private long? _lbHistoryAccount;
    private CancellationTokenSource? _lbMatchCts;
    private string _lbMatchOpen = "";

    private static readonly (string Key, string LocKey)[] LbHeaders =
    {
        ("rank", "lb_rank"),
        ("persona", "lb_player"),
        ("score", "lb_score"),
        ("kills", "lb_k"),
        ("deaths", "lb_d"),
        ("kd", "lb_kd"),
        ("damage", "lb_dmg"),
        ("accuracy", "lb_acc"),
        ("mostUsedWeapon", "lb_wpn"),
        ("mostUsedInput", "lb_in"),
        ("headshots", "lb_hs"),
        ("hits", "lb_hits"),
        ("wins", "lb_wins"),
        ("games", "lb_games"),
        ("winRate", "lb_wr"),
        ("streak", "lb_col_streak"),
        ("winStreak", "lb_col_streak"),
    };

    private void OnSimpleTabLeaderboards(object sender, RoutedEventArgs e)
    {
        ApplySimpleTab(SimpleTab.Leaderboards);
        _ = RefreshStatsSurfaceAsync(quiet: false);
    }

    private void SyncLeaderboardTab(SimpleTab tab)
    {
        var on = tab == SimpleTab.Leaderboards;
        if (PanelSimpleLeaderboards is not null)
            PanelSimpleLeaderboards.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StyleTab(BtnTabLeaderboards, on);
        EnsureLeaderboardTimer();
        EnsureActivityTimer();
        if (on)
        {
            if (_lbTimer is not null && !_lbTimer.IsEnabled)
                _lbTimer.Start();
            PaintLbSortHeaders();
            PaintLbViewTabs();
        }
        else
        {
            _lbTimer?.Stop();
        }
    }

    private void EnsureLeaderboardTimer()
    {
        if (_lbTimer is not null)
            return;
        _lbTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _lbTimer.Tick += async (_, _) =>
        {
            if (IsLoaded && _simpleTab == SimpleTab.Leaderboards)
                await RefreshStatsSurfaceAsync(quiet: true).ConfigureAwait(true);
        };
    }

    private void EnsureActivityTimer()
    {
        if (_activityTimer is not null)
            return;
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _activityTimer.Tick += async (_, _) =>
        {
            if (IsLoaded)
                await RefreshActivityAsync().ConfigureAwait(true);
        };
        _activityTimer.Start();
        _ = RefreshActivityAsync();
    }

    private void OnLbSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || string.IsNullOrWhiteSpace(key))
            return;

        key = StatsClient.NormalizeSort(key);
        if (string.Equals(_lbSort, key, StringComparison.OrdinalIgnoreCase))
        {
            _lbOrder = string.Equals(_lbOrder, "desc", StringComparison.OrdinalIgnoreCase)
                ? "asc"
                : "desc";
        }
        else
        {
            _lbSort = key;
            _lbOrder = key is "persona" or "mostUsedWeapon" or "mostUsedInput"
                ? "asc"
                : "desc";
        }

        _lbOffset = 0;
        PaintLbSortHeaders();
        if (_lbRows.Count > 0)
            BindLbFromPlayers(_lbRows.ConvertAll(r => r.Player), fetchDetails: false);
        _ = RefreshLeaderboardAsync(quiet: true);
    }

    private void OnLeaderboardRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LeaderboardRowViewModel row })
            return;
        SelectLbRow(row, fetchDetails: true);
    }

    private void OnLbHistoryRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MatchSessionViewModel row })
            return;
        e.Handled = true;
        ShowMatchDetail(row);
        _ = LoadMatchDetailAsync(row.MatchId);
    }

    private void OnLbRecentMatchClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecentMatchViewModel row })
            return;
        e.Handled = true;
        if (row.MatchId.Length == 0)
            return;
        _ = LoadMatchDetailAsync(row.MatchId);
    }

    private void OnMatchPlayerClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MatchPlayerViewModel row })
            return;
        e.Handled = true;
        if (row.AccountId <= 0)
            return;
        CloseMatchDetail();
        ShowLbBoardView();
        var hit = _lbRows.Find(r => r.AccountId == row.AccountId);
        if (hit is not null)
        {
            SelectLbRow(hit, fetchDetails: true);
            return;
        }
        _ = OpenLbPlayerAsync(row.AccountId);
    }

    private async Task LoadMatchDetailAsync(string matchId)
    {
        matchId = StatsClient.SanitizeMatchId(matchId);
        if (matchId.Length == 0)
            return;

        _lbMatchCts?.Cancel();
        _lbMatchCts = new CancellationTokenSource();
        var ct = _lbMatchCts.Token;
        _lbMatchOpen = matchId;
        OpenMatchOverlay();
        SetMatchStatus(Loc.Get("lb_loading_matches"));

        MatchDetailResult result;
        try
        {
            result = await StatsClient.GetMatchAsync(CurrentMasterServerUrl(), matchId, ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || !string.Equals(_lbMatchOpen, matchId, StringComparison.Ordinal))
            return;

        if (!result.Success || result.Match is null)
        {
            SetMatchStatus(result.Error ?? Loc.Get("lb_match_failed"));
            return;
        }

        ShowMatchDetail(new MatchSessionViewModel(result.Match, _catalog.MapNames));
        SetMatchStatus(null);
    }

    private void ShowMatchDetail(MatchSessionViewModel row)
    {
        _lbMatchOpen = row.MatchId;
        OpenMatchOverlay();
        if (TxtMatchTitle is not null)
            TxtMatchTitle.Text = row.Title;
        if (TxtMatchScore is not null)
            TxtMatchScore.Text = row.ScoreLabel;
        if (TxtMatchMeta is not null)
            TxtMatchMeta.Text = Loc.Format(
                "lb_match_meta",
                row.MapLabel,
                row.PlaylistLabel,
                row.DurationLabel,
                row.AtLabel);
        if (TxtMatchHost is not null)
            TxtMatchHost.Text = Loc.Format("lb_match_host", row.HostLabel);
        if (TxtMatchWinner is not null)
        {
            TxtMatchWinner.Text = Loc.Format("lb_match_winner", row.WinnerLabel);
            TxtMatchWinner.Visibility = row.HasWinner ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ListMatchPlayers is not null)
            ListMatchPlayers.ItemsSource = row.Players;
        if (TxtMatchOneSided is not null)
        {
            TxtMatchOneSided.Visibility = row.PlayerCount < 2
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OpenMatchOverlay()
    {
        if (OverlayLbMatch is not null)
            OverlayLbMatch.Visibility = Visibility.Visible;
    }

    private void SetMatchStatus(string? text)
    {
        if (TxtMatchStatus is null)
            return;
        if (string.IsNullOrWhiteSpace(text))
        {
            TxtMatchStatus.Text = string.Empty;
            TxtMatchStatus.Visibility = Visibility.Collapsed;
            return;
        }

        TxtMatchStatus.Text = text;
        TxtMatchStatus.Visibility = Visibility.Visible;
    }

    private void OnMatchDetailClose(object sender, RoutedEventArgs e) => CloseMatchDetail();

    private void OnMatchOverlayBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;
        e.Handled = true;
        CloseMatchDetail();
    }

    private void CloseMatchDetail()
    {
        _lbMatchCts?.Cancel();
        _lbMatchOpen = "";
        if (OverlayLbMatch is not null)
            OverlayLbMatch.Visibility = Visibility.Collapsed;
        if (ListMatchPlayers is not null)
            ListMatchPlayers.ItemsSource = null;
        SetMatchStatus(null);
    }

    private void OnLbDetailClose(object sender, RoutedEventArgs e) => CloseLbDetail();

    private void CloseLbDetail()
    {
        if (_lbSelected is not null)
            _lbSelected.IsSelected = false;
        ClearLbDetail();
    }

    /// <summary>Esc closes the match card first, then the player card.</summary>
    private bool HandleLeaderboardEscape()
    {
        if (OverlayLbMatch?.Visibility == Visibility.Visible)
        {
            CloseMatchDetail();
            return true;
        }

        if (PanelLbDetail?.Visibility == Visibility.Visible)
        {
            CloseLbDetail();
            return true;
        }

        return false;
    }

    private void OnBrowserRefreshFromLeaderboards() =>
        _ = RefreshStatsSurfaceAsync(quiet: false);

    private async Task RefreshStatsSurfaceAsync(bool quiet)
    {
        _ = RefreshActivityAsync();
        if (_lbViewHistory)
            await RefreshMatchesAsync(quiet).ConfigureAwait(true);
        else
            await RefreshLeaderboardAsync(quiet).ConfigureAwait(true);
    }

    private void PaintLbSortHeaders()
    {
        foreach (var btn in LbHeaderButtons())
        {
            if (btn is null)
                continue;
            var key = btn.Tag as string ?? string.Empty;
            var label = HeaderCaption(key);
            var on = string.Equals(key, _lbSort, StringComparison.OrdinalIgnoreCase);
            if (on)
                label += string.Equals(_lbOrder, "asc", StringComparison.OrdinalIgnoreCase) ? " ▲" : " ▼";
            btn.Content = label;
            btn.SetResourceReference(
                Control.ForegroundProperty,
                on ? "AccentBright" : "TextMuted");
        }
    }

    private IEnumerable<Button?> LbHeaderButtons()
    {
        yield return BtnLbColRank;
        yield return BtnLbColPlayer;
        yield return BtnLbColScore;
        yield return BtnLbColK;
        yield return BtnLbColD;
        yield return BtnLbColKd;
        yield return BtnLbColDmg;
        yield return BtnLbColAcc;
        yield return BtnLbColWpn;
        yield return BtnLbColIn;
        yield return BtnLbColHs;
        yield return BtnLbColHits;
        yield return BtnLbColWins;
        yield return BtnLbColGames;
        yield return BtnLbColWr;
        yield return BtnLbColStreak;
    }

    private static string HeaderCaption(string key)
    {
        foreach (var (k, locKey) in LbHeaders)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return Loc.Get(locKey);
        }
        return key;
    }

    private async Task RefreshLeaderboardAsync(bool quiet)
    {
        _lbCts?.Cancel();
        _lbCts = new CancellationTokenSource();
        var ct = _lbCts.Token;

        if (!quiet)
            SetLbStatus(Loc.Get("lb_refreshing"));

        LeaderboardResult result;
        try
        {
            await EnsureLbSeasonsAsync(ct).ConfigureAwait(true);
            result = await StatsClient.GetLeaderboardAsync(
                    CurrentMasterServerUrl(),
                    _lbSort,
                    _lbOrder,
                    StatsClient.DefaultLimit,
                    _lbOffset,
                    ct,
                    _lbSeason,
                    _lbQ)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        if (!result.Success)
        {
            SetLbStatus(result.Error ?? Loc.Get("lb_failed"));
            PaintLbPager(null);
            return;
        }

        BindLbFromPlayers(result.Players, fetchDetails: true);
        PaintLbPager(result.Pagination);

        if (_lbRows.Count == 0)
        {
            SetLbStatus(Loc.Get("lb_empty"));
            return;
        }

        var n = _lbRows.Count;
        var total = result.Pagination.Total;
        string count;
        if (total > n)
            count = Loc.Format("lb_of", n + _lbOffset, total);
        else if (total == 1 || (total == 0 && n == 1))
            count = Loc.Get("lb_player_one");
        else
            count = Loc.Format("lb_player_many", n == 0 ? total : n);
        SetLbStatus(Loc.Format("lb_updated", DateTime.Now.ToString("HH:mm:ss"), count));
    }

    private async Task RefreshMatchesAsync(bool quiet)
    {
        _lbCts?.Cancel();
        _lbCts = new CancellationTokenSource();
        var ct = _lbCts.Token;

        if (!quiet)
            SetLbStatus(Loc.Get("lb_loading_matches"));

        MatchSessionsResult result;
        try
        {
            await EnsureLbSeasonsAsync(ct).ConfigureAwait(true);
            result = await StatsClient.GetMatchSessionsAsync(
                    CurrentMasterServerUrl(),
                    _lbHistoryAccount,
                    map: null,
                    StatsClient.DefaultLimit,
                    _lbOffset,
                    ct,
                    _lbSeason)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        if (!result.Success)
        {
            SetLbStatus(result.Error ?? Loc.Get("lb_matches_failed"));
            PaintLbPager(null);
            return;
        }

        var names = _catalog.MapNames;
        var rows = new List<MatchSessionViewModel>(result.Matches.Count);
        foreach (var m in result.Matches)
            rows.Add(new MatchSessionViewModel(m, names));

        if (ListLbHistory is not null)
            ListLbHistory.ItemsSource = rows;

        PaintLbPager(result.Pagination);

        if (rows.Count == 0)
        {
            SetLbStatus(Loc.Get("lb_no_matches"));
            return;
        }

        var total = result.Pagination.Total;
        SetLbStatus(Loc.Format(
            "lb_updated",
            DateTime.Now.ToString("HH:mm:ss"),
            Loc.Format("lb_matches_n", total.ToString(CultureInfo.InvariantCulture))));
    }

    private void BindLbFromPlayers(IReadOnlyList<StatsPlayer> players, bool fetchDetails)
    {
        var selectedId = _lbSelected?.AccountId;
        var sorted = SortLocal(players);
        _lbRows.Clear();
        foreach (var p in sorted)
            _lbRows.Add(new LeaderboardRowViewModel(p));

        if (ListLb is not null)
        {
            ListLb.ItemsSource = null;
            ListLb.ItemsSource = _lbRows;
        }

        if (selectedId is long id)
        {
            var again = _lbRows.Find(r => r.AccountId == id);
            if (again is not null)
            {
                SelectLbRow(again, fetchDetails);
                return;
            }
        }

        ClearLbDetail();
    }

    private List<StatsPlayer> SortLocal(IReadOnlyList<StatsPlayer> rows)
    {
        Comparison<StatsPlayer> cmp = _lbSort switch
        {
            "rank" => (a, b) => a.Rank.CompareTo(b.Rank),
            "persona" => (a, b) => string.Compare(a.Persona, b.Persona, StringComparison.OrdinalIgnoreCase),
            "kills" => (a, b) => a.Kills.CompareTo(b.Kills),
            "deaths" => (a, b) => a.Deaths.CompareTo(b.Deaths),
            "kd" => (a, b) => a.Kd.CompareTo(b.Kd),
            "damage" => (a, b) => a.Damage.CompareTo(b.Damage),
            "accuracy" => (a, b) => a.Accuracy.CompareTo(b.Accuracy),
            "headshots" => (a, b) => a.Headshots.CompareTo(b.Headshots),
            "hits" => (a, b) => a.Hits.CompareTo(b.Hits),
            "shots" => (a, b) => a.Shots.CompareTo(b.Shots),
            "wins" => (a, b) => a.Wins.CompareTo(b.Wins),
            "losses" => (a, b) => a.Losses.CompareTo(b.Losses),
            "games" => (a, b) => a.Games.CompareTo(b.Games),
            "winRate" => (a, b) => a.WinRate.CompareTo(b.WinRate),
            "timePlayed" => (a, b) => a.TimePlayed.CompareTo(b.TimePlayed),
            "mostUsedWeapon" => (a, b) => string.Compare(a.MostUsedWeapon, b.MostUsedWeapon, StringComparison.OrdinalIgnoreCase),
            "mostUsedInput" => (a, b) => string.Compare(a.MostUsedInput, b.MostUsedInput, StringComparison.OrdinalIgnoreCase),
            "streak" or "winStreak" => (a, b) => a.CurrentWinStreak.CompareTo(b.CurrentWinStreak),
            _ => (a, b) => a.Score.CompareTo(b.Score),
        };

        var list = new List<StatsPlayer>(rows.Count);
        list.AddRange(rows);
        var desc = !string.Equals(_lbOrder, "asc", StringComparison.OrdinalIgnoreCase);
        list.Sort((a, b) =>
        {
            var c = cmp(a, b);
            if (c == 0)
                c = a.Rank.CompareTo(b.Rank);
            if (c == 0)
                c = a.AccountId.CompareTo(b.AccountId);
            return desc ? -c : c;
        });
        return list;
    }

    private void SelectLbRow(LeaderboardRowViewModel row, bool fetchDetails)
    {
        if (!ReferenceEquals(_lbSelected, row))
        {
            if (_lbSelected is not null)
                _lbSelected.IsSelected = false;
            _lbSelected = row;
            row.IsSelected = true;
        }
        else
        {
            row.IsSelected = true;
        }

        ShowLbDetail(row);
        if (fetchDetails)
            _ = LoadLbDetailsAsync(row);
    }

    private void ShowLbDetail(LeaderboardRowViewModel row)
    {
        if (PanelLbDetail is not null)
            PanelLbDetail.Visibility = _lbViewHistory ? Visibility.Collapsed : Visibility.Visible;
        if (TxtLbDetailPersona is not null)
            TxtLbDetailPersona.Text = row.Persona;
        if (TxtLbDetailRank is not null)
            TxtLbDetailRank.Text = row.Player.Rank > 0 ? Loc.Format("lb_rank_n", row.RankLabel) : Loc.Get("lb_unranked");
        if (TxtLbDetailStats is not null)
            TxtLbDetailStats.Text = Loc.Format("lb_stats", row.KdLabel, row.AccLabel, row.WrLabel);
        if (TxtLbDetailLoadout is not null)
            TxtLbDetailLoadout.Text = Loc.Format("lb_loadout", row.WeaponLabel, row.InputLabel);
        if (TxtLbDetailStreak is not null)
            TxtLbDetailStreak.Text = Loc.Format(
                "lb_streak",
                row.Player.CurrentWinStreak.ToString(CultureInfo.InvariantCulture),
                row.Player.LongestWinStreak.ToString(CultureInfo.InvariantCulture));
        if (TxtLbDetailBest is not null)
            TxtLbDetailBest.Text = Loc.Format(
                "lb_best",
                row.Player.MostKillsInMatch.ToString(CultureInfo.InvariantCulture),
                row.Player.MostDamageInMatch.ToString("N0", CultureInfo.InvariantCulture));
    }

    private void ClearLbDetail()
    {
        _lbSelected = null;
        _lbDetailCts?.Cancel();
        if (PanelLbDetail is not null)
            PanelLbDetail.Visibility = Visibility.Collapsed;
        if (ListLbMatches is not null)
            ListLbMatches.ItemsSource = null;
        if (ListLbMaps is not null)
            ListLbMaps.ItemsSource = null;
        if (ListLbWeapons is not null)
            ListLbWeapons.ItemsSource = null;
        if (TxtLbMatchesStatus is not null)
        {
            TxtLbMatchesStatus.Text = string.Empty;
            TxtLbMatchesStatus.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadLbDetailsAsync(LeaderboardRowViewModel row)
    {
        _lbDetailCts?.Cancel();
        _lbDetailCts = new CancellationTokenSource();
        var ct = _lbDetailCts.Token;
        SetLbMatchesStatus(Loc.Get("lb_loading_matches"));

        PlayerStatsResult result;
        try
        {
            result = await StatsClient.GetPlayerAsync(
                    CurrentMasterServerUrl(),
                    row.AccountId,
                    ct,
                    _lbSeason)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || _lbSelected?.AccountId != row.AccountId)
            return;

        if (!result.Success)
        {
            SetLbMatchesStatus(result.Error ?? Loc.Get("lb_matches_failed"));
            return;
        }

        if (result.Player is not null)
        {
            var fresh = new LeaderboardRowViewModel(result.Player) { IsSelected = true };
            ShowLbDetail(fresh);
        }

        BindLbSplits(result.Maps, result.Weapons);

        var names = _catalog.MapNames;
        var matches = new List<RecentMatchViewModel>();
        var src = result.RecentMatches;
        var take = Math.Min(8, src.Count);
        for (var i = 0; i < take; i++)
            matches.Add(new RecentMatchViewModel(src[i], names));

        if (ListLbMatches is not null)
            ListLbMatches.ItemsSource = matches;

        if (matches.Count == 0)
            SetLbMatchesStatus(Loc.Get("lb_no_matches"));
        else
            SetLbMatchesStatus(null);
    }

    private async Task OpenLbPlayerAsync(long accountId)
    {
        _lbDetailCts?.Cancel();
        _lbDetailCts = new CancellationTokenSource();
        var ct = _lbDetailCts.Token;
        PlayerStatsResult result;
        try
        {
            result = await StatsClient.GetPlayerAsync(
                    CurrentMasterServerUrl(),
                    accountId,
                    ct,
                    _lbSeason)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || result.Player is null)
            return;

        var row = new LeaderboardRowViewModel(result.Player) { IsSelected = true };
        if (_lbSelected is not null)
            _lbSelected.IsSelected = false;
        _lbSelected = row;
        ShowLbDetail(row);
        BindLbSplits(result.Maps, result.Weapons);
        var names = _catalog.MapNames;
        var matches = new List<RecentMatchViewModel>();
        var take = Math.Min(8, result.RecentMatches.Count);
        for (var i = 0; i < take; i++)
            matches.Add(new RecentMatchViewModel(result.RecentMatches[i], names));
        if (ListLbMatches is not null)
            ListLbMatches.ItemsSource = matches;
        SetLbMatchesStatus(matches.Count == 0 ? Loc.Get("lb_no_matches") : null);
    }

    private void BindLbSplits(IReadOnlyList<MapSplit> maps, IReadOnlyList<WeaponSplit> weapons)
    {
        var names = _catalog.MapNames;
        var mapRows = new List<SplitRowViewModel>(maps.Count);
        var takeMaps = Math.Min(6, maps.Count);
        for (var i = 0; i < takeMaps; i++)
        {
            var m = maps[i];
            var label = MapOption.PlayerName(m.Map, names);
            var extra = m.Games.ToString(CultureInfo.InvariantCulture) + "g  " +
                        m.Kills.ToString(CultureInfo.InvariantCulture) + "/" +
                        m.Deaths.ToString(CultureInfo.InvariantCulture);
            mapRows.Add(new SplitRowViewModel(label, extra));
        }

        var wpnRows = new List<SplitRowViewModel>(weapons.Count);
        var takeWpn = Math.Min(6, weapons.Count);
        for (var i = 0; i < takeWpn; i++)
        {
            var w = weapons[i];
            var extra = w.Games.ToString(CultureInfo.InvariantCulture) + "g  " +
                        w.Kills.ToString(CultureInfo.InvariantCulture) + "k";
            wpnRows.Add(new SplitRowViewModel(
                LeaderboardRowViewModel.FormatWeapon(w.Weapon), extra));
        }

        if (ListLbMaps is not null)
            ListLbMaps.ItemsSource = mapRows;
        if (ListLbWeapons is not null)
            ListLbWeapons.ItemsSource = wpnRows;
    }

    private void SetLbMatchesStatus(string? text)
    {
        if (TxtLbMatchesStatus is null)
            return;
        if (string.IsNullOrWhiteSpace(text))
        {
            TxtLbMatchesStatus.Text = string.Empty;
            TxtLbMatchesStatus.Visibility = Visibility.Collapsed;
            return;
        }

        TxtLbMatchesStatus.Text = text;
        TxtLbMatchesStatus.Visibility = Visibility.Visible;
    }

    private void SetLbStatus(string text)
    {
        if (TxtLbStatus is not null)
            TxtLbStatus.Text = text;
        if (_settings.SimpleMode && _simpleTab == SimpleTab.Leaderboards)
            SetSimpleStatus(text);
    }

    private void PaintLbPager(StatsPagination? page)
    {
        _lbHasPrev = page?.HasPrevious == true;
        _lbHasNext = page?.HasNext == true;
        if (page is not null)
            _lbOffset = Math.Max(0, page.Offset);
        if (BtnLbPrev is not null)
            BtnLbPrev.IsEnabled = _lbHasPrev;
        if (BtnLbNext is not null)
            BtnLbNext.IsEnabled = _lbHasNext;
    }

    private void OnLbPrev(object sender, RoutedEventArgs e)
    {
        if (!_lbHasPrev)
            return;
        _lbOffset = Math.Max(0, _lbOffset - StatsClient.DefaultLimit);
        _ = RefreshStatsSurfaceAsync(quiet: true);
    }

    private void OnLbNext(object sender, RoutedEventArgs e)
    {
        if (!_lbHasNext)
            return;
        _lbOffset += StatsClient.DefaultLimit;
        _ = RefreshStatsSurfaceAsync(quiet: true);
    }

    private void OnLbViewBoard(object sender, RoutedEventArgs e) => ShowLbBoardView();

    private void OnLbViewMatches(object sender, RoutedEventArgs e)
    {
        _lbHistoryAccount = null;
        ShowLbHistoryView();
    }

    private void OnLbFullHistory(object sender, RoutedEventArgs e)
    {
        _lbHistoryAccount = _lbSelected?.AccountId;
        ShowLbHistoryView();
    }

    private void ShowLbBoardView()
    {
        CloseMatchDetail();
        _lbViewHistory = false;
        _lbOffset = 0;
        if (ScrollLbBoard is not null)
            ScrollLbBoard.Visibility = Visibility.Visible;
        if (ScrollLbHistory is not null)
            ScrollLbHistory.Visibility = Visibility.Collapsed;
        PaintLbViewTabs();
        _ = RefreshLeaderboardAsync(quiet: true);
    }

    private void ShowLbHistoryView()
    {
        CloseMatchDetail();
        _lbViewHistory = true;
        _lbOffset = 0;
        if (ScrollLbBoard is not null)
            ScrollLbBoard.Visibility = Visibility.Collapsed;
        if (ScrollLbHistory is not null)
            ScrollLbHistory.Visibility = Visibility.Visible;
        if (PanelLbDetail is not null)
            PanelLbDetail.Visibility = Visibility.Collapsed;
        PaintLbViewTabs();
        _ = RefreshMatchesAsync(quiet: false);
    }

    private void PaintLbViewTabs()
    {
        StyleTab(BtnLbViewBoard, !_lbViewHistory);
        StyleTab(BtnLbViewMatches, _lbViewHistory);
    }

    private void OnLbSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _lbSearchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _lbSearchTimer.Tick -= OnLbSearchDebounced;
        _lbSearchTimer.Tick += OnLbSearchDebounced;
        _lbSearchTimer.Stop();
        _lbSearchTimer.Start();
    }

    private void OnLbSearchDebounced(object? sender, EventArgs e)
    {
        _lbSearchTimer?.Stop();
        ApplyLbSearch(fromTyping: true);
    }

    private void OnLbSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        _lbSearchTimer?.Stop();
        ApplyLbSearch(fromTyping: false);
    }

    private void ApplyLbSearch(bool fromTyping)
    {
        var next = StatsClient.SanitizeQ(TxtLbSearch?.Text);
        if (string.Equals(next, _lbQ, StringComparison.Ordinal) && fromTyping)
            return;
        _lbQ = next;
        _lbOffset = 0;
        if (_lbViewHistory)
            ShowLbBoardView();
        else
            _ = RefreshLeaderboardAsync(quiet: true);
    }

    private sealed class LbSeasonItem
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
    }

    private async Task EnsureLbSeasonsAsync(CancellationToken cancel)
    {
        if (CmbLbSeason is null)
            return;
        var got = await StatsClient.GetSeasonsAsync(CurrentMasterServerUrl(), cancel)
            .ConfigureAwait(true);
        if (!got.Success || got.Seasons.Count == 0)
            return;

        var items = new List<LbSeasonItem>();
        foreach (var s in got.Seasons)
        {
            var label = s.IsAll ? Loc.Get("lb_all_seasons") : s.Name;
            if (s.Active)
                label += " *";
            items.Add(new LbSeasonItem { Id = s.IsAll ? "all" : s.Id, Label = label });
        }

        _lbSeasonSilent = true;
        CmbLbSeason.ItemsSource = items;
        CmbLbSeason.DisplayMemberPath = nameof(LbSeasonItem.Label);
        CmbLbSeason.SelectedValuePath = nameof(LbSeasonItem.Id);
        var pick = items.Find(i => string.Equals(i.Id, _lbSeason, StringComparison.OrdinalIgnoreCase))
            ?? items.Find(i => i.Id != "all")
            ?? items[0];
        CmbLbSeason.SelectedValue = pick.Id;
        _lbSeason = pick.Id;
        _lbSeasonSilent = false;
    }

    private void OnLbSeasonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lbSeasonSilent || CmbLbSeason?.SelectedValue is not string id || id.Length == 0)
            return;
        if (string.Equals(_lbSeason, id, StringComparison.OrdinalIgnoreCase))
            return;
        _lbSeason = id;
        _lbOffset = 0;
        _ = RefreshStatsSurfaceAsync(quiet: true);
    }

    private async Task RefreshActivityAsync()
    {
        _activityCts?.Cancel();
        _activityCts = new CancellationTokenSource();
        var ct = _activityCts.Token;
        ActivityResult result;
        try
        {
            result = await StatsClient.GetActivityAsync(CurrentMasterServerUrl(), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        if (!result.Success)
        {
            PaintActivity(null);
            return;
        }

        PaintActivity(result);
    }

    private void PaintActivity(ActivityResult? stats)
    {
        string? text = null;
        if (stats is not null && stats.Success && (stats.Servers > 0 || stats.Players > 0))
            text = Loc.Format("lb_activity", stats.Servers, stats.Players);

        if (TxtLbActivity is not null)
        {
            TxtLbActivity.Text = text ?? string.Empty;
            TxtLbActivity.Visibility = string.IsNullOrEmpty(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }
}
