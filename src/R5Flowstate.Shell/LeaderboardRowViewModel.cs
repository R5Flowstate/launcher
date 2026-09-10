using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace R5Flowstate.Shell;

public sealed class LeaderboardRowViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public LeaderboardRowViewModel(StatsPlayer player)
    {
        Player = player;
        RankLabel = player.Rank > 0
            ? player.Rank.ToString(CultureInfo.InvariantCulture)
            : "--";
        Persona = string.IsNullOrWhiteSpace(player.Persona)
            ? "#" + player.AccountId.ToString(CultureInfo.InvariantCulture)
            : player.Persona.Trim();
        ScoreLabel = player.Score.ToString("N0", CultureInfo.InvariantCulture);
        KillsLabel = player.Kills.ToString(CultureInfo.InvariantCulture);
        DeathsLabel = player.Deaths.ToString(CultureInfo.InvariantCulture);
        KdLabel = player.Kd.ToString("0.00", CultureInfo.InvariantCulture);
        DamageLabel = player.Damage.ToString("N0", CultureInfo.InvariantCulture);
        AccLabel = FormatRate(player.Accuracy);
        WeaponLabel = FormatWeapon(player.MostUsedWeapon);
        InputLabel = FormatInput(player.MostUsedInput);
        HsLabel = player.Headshots.ToString(CultureInfo.InvariantCulture);
        HitsLabel = player.Hits.ToString(CultureInfo.InvariantCulture);
        WinsLabel = player.Wins.ToString(CultureInfo.InvariantCulture);
        GamesLabel = player.Games.ToString(CultureInfo.InvariantCulture);
        WrLabel = FormatRate(player.WinRate);
        StreakLabel = player.CurrentWinStreak.ToString(CultureInfo.InvariantCulture);
        IsPodium = player.Rank is > 0 and <= 3;
    }

    public StatsPlayer Player { get; }
    public long AccountId => Player.AccountId;
    public string RankLabel { get; }
    public string Persona { get; }
    public string ScoreLabel { get; }
    public string KillsLabel { get; }
    public string DeathsLabel { get; }
    public string KdLabel { get; }
    public string DamageLabel { get; }
    public string AccLabel { get; }
    public string WeaponLabel { get; }
    public string InputLabel { get; }
    public string HsLabel { get; }
    public string HitsLabel { get; }
    public string WinsLabel { get; }
    public string GamesLabel { get; }
    public string WrLabel { get; }
    public string StreakLabel { get; }
    public bool IsPodium { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static string FormatRate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return "--";
        var pct = value <= 1.0 ? value * 100.0 : value;
        return pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    internal static string FormatInput(string? raw)
    {
        var s = Dash(raw);
        if (s == "--")
            return s;
        if (s.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "--";
        if (s.Equals("controller", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("gamepad", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("pad", StringComparison.OrdinalIgnoreCase))
            return "PAD";
        if (s.Equals("mnk", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("kbm", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("mkb", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("mouse", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("pc", StringComparison.OrdinalIgnoreCase))
            return "MNK";
        return s.Length <= 4 ? s.ToUpperInvariant() : s;
    }

    internal static string FormatWeapon(string? raw)
    {
        var s = Dash(raw);
        if (s == "--")
            return s;
        if (s.StartsWith("mp_weapon_", StringComparison.OrdinalIgnoreCase))
            s = s[10..];
        return s.Replace('_', ' ');
    }

    internal static string FormatDuration(int secs)
    {
        if (secs <= 0)
            return "--";
        var m = secs / 60;
        var s = secs % 60;
        return m.ToString(CultureInfo.InvariantCulture) + ":" +
               s.ToString("00", CultureInfo.InvariantCulture);
    }

    internal static string Dash(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "--" : raw.Trim();
}

public sealed class SplitRowViewModel
{
    public SplitRowViewModel(string label, string extra)
    {
        Label = label;
        Extra = extra;
    }

    public string Label { get; }
    public string Extra { get; }
}

public sealed class RecentMatchViewModel
{
    public RecentMatchViewModel(StatsMatch match, IReadOnlyDictionary<string, string>? mapNames)
    {
        AccountId = match.AccountId;
        MatchId = StatsClient.SanitizeMatchId(match.MatchId);
        Persona = string.IsNullOrWhiteSpace(match.Persona)
            ? (match.AccountId > 0
                ? "#" + match.AccountId.ToString(CultureInfo.InvariantCulture)
                : "--")
            : match.Persona.Trim();
        Result = string.IsNullOrWhiteSpace(match.Result)
            ? "--"
            : match.Result.Trim().ToUpperInvariant();
        Opponent = string.IsNullOrWhiteSpace(match.OpponentPersona)
            ? (match.OpponentId is long oid and > 0
                ? "#" + oid.ToString(CultureInfo.InvariantCulture)
                : "--")
            : match.OpponentPersona.Trim();
        MapLabel = string.IsNullOrWhiteSpace(match.Map)
            ? "--"
            : MapOption.PlayerName(match.Map, mapNames);
        KdLabel = match.Kills.ToString(CultureInfo.InvariantCulture) +
                  "-" +
                  match.Deaths.ToString(CultureInfo.InvariantCulture);
        DamageLabel = match.Damage.ToString("N0", CultureInfo.InvariantCulture);
        WeaponLabel = LeaderboardRowViewModel.FormatWeapon(match.Weapon);
        DurationLabel = LeaderboardRowViewModel.FormatDuration(match.Duration);
        HostLabel = LeaderboardRowViewModel.Dash(match.HostName);
        AtLabel = FormatAt(match.At);
        IsWin = Result is "W" or "WIN";
        IsLoss = Result is "L" or "LOSS";
    }

    public string MatchId { get; } = string.Empty;
    public long AccountId { get; }
    public string Persona { get; }
    public string Result { get; }
    public string Opponent { get; }
    public string MapLabel { get; }
    public string KdLabel { get; }
    public string DamageLabel { get; }
    public string WeaponLabel { get; }
    public string DurationLabel { get; }
    public string HostLabel { get; }
    public string AtLabel { get; }
    public bool IsWin { get; }
    public bool IsLoss { get; }

    private static string FormatAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t))
            return t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return raw.Trim();
    }
}

public sealed class MatchPlayerViewModel
{
    public MatchPlayerViewModel(StatsMatchPlayer player, bool isWinner)
    {
        AccountId = player.AccountId;
        Persona = string.IsNullOrWhiteSpace(player.Persona)
            ? "#" + player.AccountId.ToString(CultureInfo.InvariantCulture)
            : player.Persona.Trim();
        Result = string.IsNullOrWhiteSpace(player.Result)
            ? "--"
            : player.Result.Trim().ToUpperInvariant();
        KillsLabel = player.Kills.ToString(CultureInfo.InvariantCulture);
        DeathsLabel = player.Deaths.ToString(CultureInfo.InvariantCulture);
        DamageLabel = player.Damage.ToString("N0", CultureInfo.InvariantCulture);
        ShotsLabel = player.Shots.ToString(CultureInfo.InvariantCulture);
        HitsLabel = player.Hits.ToString(CultureInfo.InvariantCulture);
        HsLabel = player.Headshots.ToString(CultureInfo.InvariantCulture);
        AccLabel = LeaderboardRowViewModel.FormatRate(player.Accuracy);
        WeaponLabel = LeaderboardRowViewModel.FormatWeapon(player.Weapon);
        InputLabel = LeaderboardRowViewModel.FormatInput(player.Input);
        IsWinner = isWinner;
        IsWin = Result is "W" or "WIN";
        IsLoss = Result is "L" or "LOSS";
    }

    public long AccountId { get; }
    public string Persona { get; }
    public string Result { get; }
    public string KillsLabel { get; }
    public string DeathsLabel { get; }
    public string DamageLabel { get; }
    public string ShotsLabel { get; }
    public string HitsLabel { get; }
    public string HsLabel { get; }
    public string AccLabel { get; }
    public string WeaponLabel { get; }
    public string InputLabel { get; }
    public bool IsWinner { get; }
    public bool IsWin { get; }
    public bool IsLoss { get; }
}

public sealed class MatchSessionViewModel
{
    public MatchSessionViewModel(
        StatsMatchSession session,
        IReadOnlyDictionary<string, string>? mapNames)
    {
        Session = session;
        MatchId = StatsClient.SanitizeMatchId(session.MatchId);
        MapLabel = string.IsNullOrWhiteSpace(session.Map)
            ? "--"
            : MapOption.PlayerName(session.Map, mapNames);
        PlaylistLabel = LeaderboardRowViewModel.Dash(session.Playlist);
        HostLabel = LeaderboardRowViewModel.Dash(session.HostName);
        DurationLabel = LeaderboardRowViewModel.FormatDuration(session.Duration);
        AtLabel = FormatAt(session.At);
        KillsLabel = session.Kills.ToString(CultureInfo.InvariantCulture);
        DamageLabel = session.Damage.ToString("N0", CultureInfo.InvariantCulture);

        var players = new List<MatchPlayerViewModel>(session.Players.Length);
        foreach (var p in session.Players)
            players.Add(new MatchPlayerViewModel(p, session.WinnerId == p.AccountId));
        Players = players;

        WinnerLabel = string.IsNullOrWhiteSpace(session.WinnerPersona)
            ? (session.WinnerId is long id and > 0
                ? "#" + id.ToString(CultureInfo.InvariantCulture)
                : "--")
            : session.WinnerPersona.Trim();

        // A one-sided row is a legacy match: it predates session grouping, or the
        // host only reported one participant.
        Title = players.Count switch
        {
            0 => "--",
            1 => players[0].Persona,
            2 => players[0].Persona + "  " + Loc.Get("vs") + players[1].Persona,
            _ => Loc.Format("lb_match_players_n", players.Count),
        };

        ScoreLabel = players.Count switch
        {
            0 => "--",
            1 => players[0].KillsLabel,
            2 => players[0].KillsLabel + " - " + players[1].KillsLabel,
            _ => KillsLabel,
        };

        PlayerCount = players.Count;
        HasWinner = session.WinnerId is long w && w > 0;
    }

    public StatsMatchSession Session { get; }
    public string MatchId { get; }
    public string Title { get; }
    public string ScoreLabel { get; }
    public string MapLabel { get; }
    public string PlaylistLabel { get; }
    public string HostLabel { get; }
    public string DurationLabel { get; }
    public string AtLabel { get; }
    public string KillsLabel { get; }
    public string DamageLabel { get; }
    public string WinnerLabel { get; }
    public int PlayerCount { get; }
    public bool HasWinner { get; }
    public IReadOnlyList<MatchPlayerViewModel> Players { get; }

    private static string FormatAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t))
            return t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return raw.Trim();
    }
}
