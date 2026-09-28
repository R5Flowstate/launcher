using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>
/// Leaderboards tab — port of the Windows launcher's MainWindow.Leaderboards.cs
/// (board, history, seasons, search, paging, the player card and the match card)
/// over the ported <see cref="StatsClient"/>.
///
/// What moved and why: Windows drives all of this from code-behind timers and
/// event handlers. Here the state and the fetching live in the view model and the
/// window keeps only what a view must own — the 400 ms search debounce, the two
/// poll timers, and painting the header captions from the sort state. The
/// requests, the ordering rules, the pager arithmetic and every label are
/// upstream's.
///
/// Read-only by construction: every call is a GET on the public stats routes.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Upstream's column table: wire sort key → loc key.</summary>
    public static readonly (string Key, string LocKey)[] LbHeaders =
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

    string _lbSort = "score";
    string _lbOrder = "desc";
    string _lbSeason = "current";
    string _lbQ = "";
    int _lbOffset;
    bool _lbViewHistory;
    long? _lbHistoryAccount;
    LeaderboardRowVM? _lbSelected;
    CancellationTokenSource? _lbCts;
    CancellationTokenSource? _lbDetailCts;
    CancellationTokenSource? _lbMatchCts;
    CancellationTokenSource? _lbActivityCts;
    string _lbMatchOpen = "";

    /// <summary>Wire sort key currently in force (the header painter reads it).</summary>
    public string LbSortKey => _lbSort;

    /// <summary>"asc" or "desc"; the header painter draws ▲ / ▼ from it.</summary>
    public string LbOrder => _lbOrder;

    /// <summary>True while the history list is the visible one.</summary>
    public bool LbViewHistory => _lbViewHistory;

    /// <summary>Season id in force; "current" until the server names one.</summary>
    public string LbSeason => _lbSeason;

    /// <summary>Sanitised search text in force.</summary>
    public string LbQuery => _lbQ;

    [ObservableProperty] private ObservableCollection<MatchSessionVM> _leaderboardHistoryRows = new();
    [ObservableProperty] private ObservableCollection<SplitRowVM> _lbMapSplits = new();
    [ObservableProperty] private ObservableCollection<SplitRowVM> _lbWeaponSplits = new();
    [ObservableProperty] private ObservableCollection<RecentMatchVM> _lbRecentMatches = new();
    [ObservableProperty] private ObservableCollection<LbSeasonVM> _lbSeasons = new();
    [ObservableProperty] private LbSeasonVM? _lbSelectedSeason;
    [ObservableProperty] private bool _lbHasNext;
    [ObservableProperty] private bool _lbHasPrev;

    /// <summary>Header activity line ("12 servers · 40 players"), empty when the
    /// server reports none — the view hides the line rather than showing a zero.</summary>
    [ObservableProperty] private string _lbActivity = "";

    partial void OnLbActivityChanged(string value) =>
        OnPropertyChanged(nameof(LbActivityVisible));

    /// <summary>True when the master server reported activity — the view hides the
    /// line rather than printing a zero.</summary>
    public bool LbActivityVisible => !string.IsNullOrWhiteSpace(LbActivity);

    // ---- player card ----
    [ObservableProperty] private bool _lbDetailVisible;
    [ObservableProperty] private string _lbDetailPersona = "";
    [ObservableProperty] private string _lbDetailRank = "";
    [ObservableProperty] private string _lbDetailStats = "";
    [ObservableProperty] private string _lbDetailLoadout = "";
    [ObservableProperty] private string _lbDetailStreak = "";
    [ObservableProperty] private string _lbDetailBest = "";
    [ObservableProperty] private string _lbMatchesStatus = "";

    partial void OnLbMatchesStatusChanged(string value) =>
        OnPropertyChanged(nameof(LbMatchesStatusVisible));

    /// <summary>True while the card has something to say (the view collapses the
    /// empty line instead of leaving a blank row).</summary>
    public bool LbMatchesStatusVisible => !string.IsNullOrWhiteSpace(LbMatchesStatus);

    // ---- match card ----
    [ObservableProperty] private bool _lbMatchVisible;
    [ObservableProperty] private string _lbMatchTitle = "";
    [ObservableProperty] private string _lbMatchScore = "";
    [ObservableProperty] private string _lbMatchMeta = "";
    [ObservableProperty] private string _lbMatchHost = "";
    [ObservableProperty] private string _lbMatchWinner = "";
    [ObservableProperty] private string _lbMatchStatus = "";
    [ObservableProperty] private bool _lbMatchHasWinner;
    [ObservableProperty] private bool _lbMatchOneSided;
    [ObservableProperty] private ObservableCollection<MatchPlayerVM> _lbMatchPlayers = new();

    partial void OnLbMatchStatusChanged(string value) =>
        OnPropertyChanged(nameof(LbMatchStatusVisible));

    public bool LbMatchStatusVisible => !string.IsNullOrWhiteSpace(LbMatchStatus);

    /// <summary>
    /// The install's stem → friendly-name table, from the playlist catalog — the
    /// same call the browser makes. An empty table (no install, or no catalog
    /// yet) reads exactly as Windows does before its catalog loads: the
    /// prettified stem, never an invented name.
    /// </summary>
    IReadOnlyDictionary<string, string> MapNames => CatalogMapNames;

    void InitLeaderboards()
    {
        // Windows starts on the board view with the score column descending and a
        // blank status line; the season combo, pager and rows are filled by the
        // first refresh. InitBrowser's counterpart.
        LbStatus = "";
        PaintLbSortHeaders();
    }

    /// <summary>The header caption for a wire sort key, with ▲/▼ when it is the
    /// active one. The view binds the buttons' content to this.</summary>
    public string LbHeaderCaption(string key)
    {
        var caption = key;
        foreach (var (k, locKey) in LbHeaders)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                caption = Loc.Get(locKey);
                break;
            }
        }
        if (!string.Equals(key, _lbSort, StringComparison.OrdinalIgnoreCase))
            return caption;
        return caption + (string.Equals(_lbOrder, "asc", StringComparison.OrdinalIgnoreCase)
            ? " ▲"
            : " ▼");
    }

    /// <summary>True when this column is the active sort — the view paints it
    /// AccentBright instead of TextMuted.</summary>
    public bool LbHeaderIsActive(string key) =>
        string.Equals(key, _lbSort, StringComparison.OrdinalIgnoreCase);

    /// <summary>Raises the notifications the header painter listens for.</summary>
    void PaintLbSortHeaders() => LbSortChanged?.Invoke();

    /// <summary>The window re-captions its column buttons when this fires.</summary>
    public event Action? LbSortChanged;

    // ---------------------------------------------------------------- commands

    /// <summary>Refresh button / tab open / Enter in the search box.</summary>
    [RelayCommand]
    private Task RefreshLeaderboardsAsync() => RefreshStatsSurfaceAsync(quiet: false);

    /// <summary>Click on a column header: same column flips the order, a new
    /// column starts descending (ascending for text columns, as Windows does).</summary>
    [RelayCommand]
    private void LbSort(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        var next = StatsClient.NormalizeSort(key);
        if (string.Equals(_lbSort, next, StringComparison.OrdinalIgnoreCase))
        {
            _lbOrder = string.Equals(_lbOrder, "desc", StringComparison.OrdinalIgnoreCase)
                ? "asc"
                : "desc";
        }
        else
        {
            _lbSort = next;
            _lbOrder = next is "persona" or "mostUsedWeapon" or "mostUsedInput"
                ? "asc"
                : "desc";
        }

        _lbOffset = 0;
        PaintLbSortHeaders();
        // Repaint from the rows already held so the order flips immediately, then
        // re-ask the server (which sorts globally, not just this page).
        if (LeaderboardRows.Count > 0)
            BindLbFromPlayers(LeaderboardRows.Select(r => r.Player).ToList(), fetchDetails: false);
        _ = RefreshLeaderboardAsync(quiet: true);
    }

    [RelayCommand]
    private void LbPrev()
    {
        if (!LbHasPrev)
            return;
        _lbOffset = Math.Max(0, _lbOffset - StatsClient.DefaultLimit);
        _ = RefreshStatsSurfaceAsync(quiet: true);
    }

    [RelayCommand]
    private void LbNext()
    {
        if (!LbHasNext)
            return;
        _lbOffset += StatsClient.DefaultLimit;
        _ = RefreshStatsSurfaceAsync(quiet: true);
    }

    [RelayCommand]
    private void LbShowBoard() => ShowLbBoardView();

    [RelayCommand]
    private void LbShowHistory()
    {
        _lbHistoryAccount = null;
        ShowLbHistoryView();
    }

    /// <summary>"Full history" in the player card: the history list filtered to
    /// that account.</summary>
    [RelayCommand]
    private void LbFullHistory()
    {
        _lbHistoryAccount = _lbSelected?.AccountId;
        ShowLbHistoryView();
    }

    /// <summary>Click on a board row: open the card and load the details.</summary>
    [RelayCommand]
    private void LbSelectRow(LeaderboardRowVM? row)
    {
        if (row is null)
            return;
        SelectLbRow(row, fetchDetails: true);
    }

    [RelayCommand]
    private void LbCloseDetail() => CloseLbDetail();

    /// <summary>Season picked in the combo. The silent-paint flag is the view's
    /// (it sets SelectedItem while filling the list), so the command only runs
    /// when the player really changed it.</summary>
    [RelayCommand]
    private void LbSeasonChanged(LbSeasonVM? season)
    {
        if (season is null || season.Id.Length == 0)
            return;
        if (string.Equals(_lbSeason, season.Id, StringComparison.OrdinalIgnoreCase))
            return;
        _lbSeason = season.Id;
        _lbOffset = 0;
        _ = RefreshStatsSurfaceAsync(quiet: true);
    }

    /// <summary>Search box (after the view's 400 ms debounce, or on Enter).</summary>
    [RelayCommand]
    private void LbSearch(string? text)
    {
        var next = StatsClient.SanitizeQ(text);
        if (string.Equals(next, _lbQ, StringComparison.Ordinal))
            return;
        _lbQ = next;
        _lbOffset = 0;
        // Windows drops back to the board when you search from the history view.
        if (_lbViewHistory)
            ShowLbBoardView();
        else
            _ = RefreshLeaderboardAsync(quiet: true);
    }

    /// <summary>Click on a history row or a recent match: open the match card.</summary>
    [RelayCommand]
    private void LbOpenMatch(MatchSessionVM? row)
    {
        if (row is null)
            return;
        ShowMatchDetail(row);
        _ = LoadMatchDetailAsync(row.MatchId);
    }

    [RelayCommand]
    private void LbOpenRecentMatch(RecentMatchVM? row)
    {
        if (row is null || row.MatchId.Length == 0)
            return;
        _ = LoadMatchDetailAsync(row.MatchId);
    }

    /// <summary>Click on a player inside the match card: close it, go back to the
    /// board and open that player — from the loaded page if they are on it.</summary>
    [RelayCommand]
    private void LbOpenMatchPlayer(MatchPlayerVM? row)
    {
        if (row is null || row.AccountId <= 0)
            return;
        CloseMatchDetail();
        ShowLbBoardView();
        var hit = LeaderboardRows.FirstOrDefault(r => r.AccountId == row.AccountId);
        if (hit is not null)
        {
            SelectLbRow(hit, fetchDetails: true);
            return;
        }
        _ = OpenLbPlayerAsync(row.AccountId);
    }

    [RelayCommand]
    private void LbCloseMatch() => CloseMatchDetail();

    /// <summary>Esc closes the match card first, then the player card. True when
    /// something was closed, so the window knows not to close itself.</summary>
    public bool HandleLeaderboardEscape()
    {
        if (LbMatchVisible)
        {
            CloseMatchDetail();
            return true;
        }

        if (LbDetailVisible)
        {
            CloseLbDetail();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Quiet poll for the tab's 30 s timer: whichever view is up, never touching
    /// the status line — a poll that rewrote it would repaint it for no reason.
    /// </summary>
    public async Task PollLeaderboardsAsync()
    {
        if (_lbViewHistory)
            await RefreshMatchesAsync(quiet: true).ConfigureAwait(true);
        else
            await RefreshLeaderboardAsync(quiet: true).ConfigureAwait(true);
    }

    /// <summary>Quiet poll for the 15 s timer: the header activity line only.</summary>
    public Task PollActivityAsync() => RefreshActivityAsync();

    // ------------------------------------------------------------- board / history

    async Task RefreshStatsSurfaceAsync(bool quiet)
    {
        _ = RefreshActivityAsync();
        if (_lbViewHistory)
            await RefreshMatchesAsync(quiet).ConfigureAwait(true);
        else
            await RefreshLeaderboardAsync(quiet).ConfigureAwait(true);
    }

    async Task RefreshLeaderboardAsync(bool quiet)
    {
        _lbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lbCts = cts;
        var ct = cts.Token;

        if (!quiet)
            SetLbStatus(Loc.Get("lb_refreshing"));

        LeaderboardResult result;
        try
        {
            await EnsureLbSeasonsAsync(ct).ConfigureAwait(true);
            result = await StatsClient.GetLeaderboardAsync(
                    MasterServerUrl,
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

        if (LeaderboardRows.Count == 0)
        {
            SetLbStatus(Loc.Get("lb_empty"));
            return;
        }

        var n = LeaderboardRows.Count;
        var total = result.Pagination.Total;
        string count;
        if (total > n)
            count = Loc.Format("lb_of", n + _lbOffset, total);
        else if (total == 1 || (total == 0 && n == 1))
            count = Loc.Get("lb_player_one");
        else
            count = Loc.Format("lb_player_many", n == 0 ? total : n);
        SetLbStatus(Loc.Format("lb_updated", DateTime.Now.ToString("HH:mm:ss"), count));

        if (!quiet)
            AppendLog($"Leaderboard: {n} row(s), sort {_lbSort} {_lbOrder}, season {_lbSeason}.");
    }

    async Task RefreshMatchesAsync(bool quiet)
    {
        _lbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lbCts = cts;
        var ct = cts.Token;

        if (!quiet)
            SetLbStatus(Loc.Get("lb_loading_matches"));

        MatchSessionsResult result;
        try
        {
            await EnsureLbSeasonsAsync(ct).ConfigureAwait(true);
            result = await StatsClient.GetMatchSessionsAsync(
                    MasterServerUrl,
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

        var names = MapNames;
        var rows = new List<MatchSessionVM>(result.Matches.Count);
        foreach (var m in result.Matches)
            rows.Add(new MatchSessionVM(m, names));

        LeaderboardHistoryRows.Clear();
        foreach (var row in rows)
            LeaderboardHistoryRows.Add(row);

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

        if (!quiet)
            AppendLog($"Match history: {rows.Count} match(es) listed.");
    }

    internal void BindLbFromPlayers(IReadOnlyList<StatsPlayer> players, bool fetchDetails)
    {
        var selectedId = _lbSelected?.AccountId;
        var sorted = SortLocal(_lbSort, _lbOrder, players);
        LeaderboardRows.Clear();
        foreach (var p in sorted)
            LeaderboardRows.Add(new LeaderboardRowVM(p));

        if (selectedId is long id)
        {
            var again = LeaderboardRows.FirstOrDefault(r => r.AccountId == id);
            if (again is not null)
            {
                SelectLbRow(again, fetchDetails);
                return;
            }
        }

        ClearLbDetail();
    }

    /// <summary>Upstream's local ordering (the server sorts globally, this keeps
    /// the page the player is looking at in the same order while a request is in
    /// flight, and breaks ties on rank then account id). Static and parameterised
    /// on the sort state so the ordering rules can be checked without a socket —
    /// the behaviour is upstream's SortLocal, unchanged.</summary>
    internal static List<StatsPlayer> SortLocal(
        string sort, string order, IReadOnlyList<StatsPlayer> rows)
    {
        Comparison<StatsPlayer> cmp = sort switch
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
        var desc = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
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

    void ShowLbBoardView()
    {
        CloseMatchDetail();
        _lbViewHistory = false;
        _lbOffset = 0;
        NotifyLbViewChanged();
        _ = RefreshLeaderboardAsync(quiet: true);
    }

    void ShowLbHistoryView()
    {
        CloseMatchDetail();
        _lbViewHistory = true;
        _lbOffset = 0;
        // The card belongs to the board view; Windows collapses it here too.
        ClearLbDetail();
        NotifyLbViewChanged();
        _ = RefreshMatchesAsync(quiet: false);
    }

    void NotifyLbViewChanged()
    {
        OnPropertyChanged(nameof(LbViewHistory));
        LbViewChanged?.Invoke();
    }

    /// <summary>The window flips the two scroll hosts and the view tabs when this
    /// fires.</summary>
    public event Action? LbViewChanged;

    internal void PaintLbPager(StatsPagination? page)
    {
        LbHasPrev = page?.HasPrevious == true;
        LbHasNext = page?.HasNext == true;
        if (page is not null)
            _lbOffset = Math.Max(0, page.Offset);
    }

    // --------------------------------------------------------------- player card

    void SelectLbRow(LeaderboardRowVM row, bool fetchDetails)
    {
        if (!ReferenceEquals(_lbSelected, row))
        {
            if (_lbSelected is not null)
                _lbSelected.IsSelected = false;
            _lbSelected = row;
        }

        row.IsSelected = true;
        ShowLbDetail(row);
        if (fetchDetails)
            _ = LoadLbDetailsAsync(row);
    }

    void ShowLbDetail(LeaderboardRowVM row)
    {
        LbDetailVisible = !_lbViewHistory;
        LbDetailPersona = row.Persona;
        LbDetailRank = row.Player.Rank > 0
            ? Loc.Format("lb_rank_n", row.RankLabel)
            : Loc.Get("lb_unranked");
        LbDetailStats = Loc.Format("lb_stats", row.KdLabel, row.AccLabel, row.WrLabel);
        LbDetailLoadout = Loc.Format("lb_loadout", row.WeaponLabel, row.InputLabel);
        LbDetailStreak = Loc.Format(
            "lb_streak",
            row.Player.CurrentWinStreak.ToString(CultureInfo.InvariantCulture),
            row.Player.LongestWinStreak.ToString(CultureInfo.InvariantCulture));
        LbDetailBest = Loc.Format(
            "lb_best",
            row.Player.MostKillsInMatch.ToString(CultureInfo.InvariantCulture),
            row.Player.MostDamageInMatch.ToString("N0", CultureInfo.InvariantCulture));
    }

    void CloseLbDetail() => ClearLbDetail();

    void ClearLbDetail()
    {
        if (_lbSelected is not null)
        {
            _lbSelected.IsSelected = false;
            _lbSelected = null;
        }

        _lbDetailCts?.Cancel();
        LbDetailVisible = false;
        LbRecentMatches.Clear();
        LbMapSplits.Clear();
        LbWeaponSplits.Clear();
        LbMatchesStatus = "";
    }

    async Task LoadLbDetailsAsync(LeaderboardRowVM row)
    {
        _lbDetailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lbDetailCts = cts;
        var ct = cts.Token;
        LbMatchesStatus = Loc.Get("lb_loading_matches");

        PlayerStatsResult result;
        try
        {
            result = await StatsClient.GetPlayerAsync(MasterServerUrl, row.AccountId, ct, _lbSeason)
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
            LbMatchesStatus = result.Error ?? Loc.Get("lb_matches_failed");
            return;
        }

        // The player route carries the authoritative numbers (the board row is a
        // page snapshot), so the card repaints from it.
        if (result.Player is not null)
            ShowLbDetail(new LeaderboardRowVM(result.Player) { IsSelected = true });

        BindLbSplits(result.Maps, result.Weapons);

        var names = MapNames;
        var matches = new List<RecentMatchVM>();
        var take = Math.Min(8, result.RecentMatches.Count);
        for (var i = 0; i < take; i++)
            matches.Add(new RecentMatchVM(result.RecentMatches[i], names));

        LbRecentMatches.Clear();
        foreach (var m in matches)
            LbRecentMatches.Add(m);

        LbMatchesStatus = matches.Count == 0 ? Loc.Get("lb_no_matches") : "";
    }

    /// <summary>Opens a player who is not on the loaded page (clicked inside a
    /// match card). No board row exists for them, so the card is built from the
    /// player route alone.</summary>
    async Task OpenLbPlayerAsync(long accountId)
    {
        _lbDetailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lbDetailCts = cts;
        var ct = cts.Token;
        PlayerStatsResult result;
        try
        {
            result = await StatsClient.GetPlayerAsync(MasterServerUrl, accountId, ct, _lbSeason)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || result.Player is null)
            return;

        if (_lbSelected is not null)
            _lbSelected.IsSelected = false;
        _lbSelected = null;
        var row = new LeaderboardRowVM(result.Player) { IsSelected = true };
        ShowLbDetail(row);
        BindLbSplits(result.Maps, result.Weapons);

        var names = MapNames;
        var matches = new List<RecentMatchVM>();
        var take = Math.Min(8, result.RecentMatches.Count);
        for (var i = 0; i < take; i++)
            matches.Add(new RecentMatchVM(result.RecentMatches[i], names));

        LbRecentMatches.Clear();
        foreach (var m in matches)
            LbRecentMatches.Add(m);
        LbMatchesStatus = matches.Count == 0 ? Loc.Get("lb_no_matches") : "";
    }

    void BindLbSplits(IReadOnlyList<MapSplit> maps, IReadOnlyList<WeaponSplit> weapons)
    {
        var names = MapNames;

        LbMapSplits.Clear();
        var takeMaps = Math.Min(6, maps.Count);
        for (var i = 0; i < takeMaps; i++)
        {
            var m = maps[i];
            var label = MapLabels.PlayerName(m.Map, names);
            var extra = m.Games.ToString(CultureInfo.InvariantCulture) + "g  " +
                        m.Kills.ToString(CultureInfo.InvariantCulture) + "/" +
                        m.Deaths.ToString(CultureInfo.InvariantCulture);
            LbMapSplits.Add(new SplitRowVM(label, extra));
        }

        LbWeaponSplits.Clear();
        var takeWpn = Math.Min(6, weapons.Count);
        for (var i = 0; i < takeWpn; i++)
        {
            var w = weapons[i];
            var extra = w.Games.ToString(CultureInfo.InvariantCulture) + "g  " +
                        w.Kills.ToString(CultureInfo.InvariantCulture) + "k";
            LbWeaponSplits.Add(new SplitRowVM(LeaderboardRowVM.FormatWeapon(w.Weapon), extra));
        }
    }

    // ---------------------------------------------------------------- match card

    async Task LoadMatchDetailAsync(string matchId)
    {
        matchId = StatsClient.SanitizeMatchId(matchId);
        if (matchId.Length == 0)
            return;

        _lbMatchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lbMatchCts = cts;
        var ct = cts.Token;
        _lbMatchOpen = matchId;
        LbMatchVisible = true;
        LbMatchStatus = Loc.Get("lb_loading_matches");

        MatchDetailResult result;
        try
        {
            result = await StatsClient.GetMatchAsync(MasterServerUrl, matchId, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || !string.Equals(_lbMatchOpen, matchId, StringComparison.Ordinal))
            return;

        if (!result.Success || result.Match is null)
        {
            LbMatchStatus = result.Error ?? Loc.Get("lb_match_failed");
            return;
        }

        ShowMatchDetail(new MatchSessionVM(result.Match, MapNames));
        LbMatchStatus = "";
    }

    void ShowMatchDetail(MatchSessionVM row)
    {
        _lbMatchOpen = row.MatchId;
        LbMatchVisible = true;
        LbMatchTitle = row.Title;
        LbMatchScore = row.ScoreLabel;
        LbMatchMeta = Loc.Format(
            "lb_match_meta",
            row.MapLabel,
            row.PlaylistLabel,
            row.DurationLabel,
            row.AtLabel);
        LbMatchHost = Loc.Format("lb_match_host", row.HostLabel);
        LbMatchWinner = Loc.Format("lb_match_winner", row.WinnerLabel);
        LbMatchHasWinner = row.HasWinner;
        LbMatchOneSided = row.PlayerCount < 2;

        LbMatchPlayers.Clear();
        foreach (var p in row.Players)
            LbMatchPlayers.Add(p);
    }

    void CloseMatchDetail()
    {
        _lbMatchCts?.Cancel();
        _lbMatchOpen = "";
        LbMatchVisible = false;
        LbMatchPlayers.Clear();
        LbMatchStatus = "";
    }

    // ------------------------------------------------------------------- seasons

    async Task EnsureLbSeasonsAsync(CancellationToken cancel)
    {
        SeasonsResult got;
        try
        {
            got = await StatsClient.GetSeasonsAsync(MasterServerUrl, cancel).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (!got.Success || got.Seasons.Count == 0)
            return;

        // Rebuilding the list under the player's cursor would reset the combo, so
        // only repaint when the server actually names something new.
        var items = SeasonItems(got.Seasons);
        if (LbSelectedSeason is not null && SameSeasons(items, LbSeasons))
            return;

        LbSeasons.Clear();
        foreach (var item in items)
            LbSeasons.Add(item);

        var pick = PickSeason(LbSeasons, _lbSeason);
        _lbSeason = pick.Id;
        LbSelectedSeason = pick;
    }

    /// <summary>Wire seasons → picker rows ("all" for the all-seasons pseudo
    /// season, " *" on the active one). Windows builds these inline in
    /// EnsureLbSeasonsAsync.</summary>
    internal static List<LbSeasonVM> SeasonItems(IReadOnlyList<StatsSeason> seasons)
    {
        var items = new List<LbSeasonVM>(seasons.Count);
        foreach (var s in seasons)
            items.Add(new LbSeasonVM(s.IsAll ? "all" : s.Id, s.Name, s.Active, s.IsAll));
        return items;
    }

    /// <summary>The season to keep selected: the one in force, else the first real
    /// season, else the first entry (upstream's pick, in the same order).</summary>
    internal static LbSeasonVM PickSeason(IReadOnlyList<LbSeasonVM> items, string current)
    {
        foreach (var i in items)
            if (string.Equals(i.Id, current, StringComparison.OrdinalIgnoreCase))
                return i;
        foreach (var i in items)
            if (i.Id != "all")
                return i;
        return items[0];
    }

    /// <summary>True when two season lists would render identically.</summary>
    internal static bool SameSeasons(
        IReadOnlyList<LbSeasonVM> a, IReadOnlyList<LbSeasonVM> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Label != b[i].Label)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ activity

    async Task RefreshActivityAsync()
    {
        _lbActivityCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lbActivityCts = cts;
        var ct = cts.Token;

        ActivityResult result;
        try
        {
            result = await StatsClient.GetActivityAsync(MasterServerUrl, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        LbActivity = result.Success && result.Servers > 0 || result.Success && result.Players > 0
            ? Loc.Format("lb_activity", result.Servers, result.Players)
            : "";
    }

    // -------------------------------------------------------------------- status

    void SetLbStatus(string text)
    {
        LbStatus = text;
        // Windows mirrors the line into the simple footer while the tab is up.
        if (IsSimpleMode)
            SimpleStatus = text;
    }
}
