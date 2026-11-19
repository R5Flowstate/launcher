using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>One mode in the Simple-mode rail. The data is the ported
/// ModeCardViewModel's, so the rail cannot disagree with the console's mode
/// picker or the hero about which mode is selected; this class adds only what
/// Avalonia needs that Windows did with DataTriggers: the resolved brushes.
/// Windows styled selection with a DataTrigger on IsSelected; on Linux the item
/// exposes the resolved brushes so the XAML template stays declarative.</summary>
public sealed class ModeItemVM : ObservableObject
{
    public ModeCardViewModel Card { get; }

    public string Id => Card.Id;
    public string Group => Card.Group;
    public string Title => Card.Title;
    public string Blurb => Card.Blurb;
    public string MapCountLabel => Card.MapCountLabel;
    public bool IsSelected => Card.IsSelected;

    public ModeItemVM(ModeCardViewModel card)
    {
        Card = card;
        card.PropertyChanged += OnCardChanged;
    }

    void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ModeCardViewModel.IsSelected)
            and not nameof(ModeCardViewModel.SelectedMap))
        {
            return;
        }
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(MapCountLabel));
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipForeground));
        OnPropertyChanged(nameof(MapCountForeground));
        OnPropertyChanged(nameof(ChipWeight));
    }

    public IBrush ChipBackground => IsSelected ? ThemeBrushes.AccentTrack : ThemeBrushes.Transparent;
    public IBrush ChipForeground => IsSelected ? ThemeBrushes.AccentBright : ThemeBrushes.TextSecondary;
    public IBrush MapCountForeground => IsSelected ? ThemeBrushes.Accent : ThemeBrushes.TextMuted;
    public FontWeight ChipWeight => IsSelected ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>Groups have no Avalonia GroupStyle equivalent; the rail binds a
/// flat list of groups, each rendering its own header (same visual).</summary>
public sealed class ModeGroupVM
{
    public string Header { get; }
    public ObservableCollection<ModeItemVM> Items { get; } = new();
    public string ItemCount => Items.Count.ToString();
    public ModeGroupVM(string header) { Header = header; }
}

/// <summary>Loadscreen thumbnail in the map picker.</summary>
public partial class MapTileVM : ObservableObject
{
    public string DisplayName { get; }
    public string Stem { get; }

    /// <summary>The map's loadscreen thumbnail. Observable, not ctor-only:
    /// decoding 196px of rpak takes long enough that the tiles are built and
    /// shown first and the art lands on them a moment later (Windows does the
    /// same with MapTileViewModel.Art).</summary>
    [ObservableProperty] private IImage? _art;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(RingOpacity));
    public double RingOpacity => IsSelected ? 1 : 0;

    /// <summary>The catalog option this tile stands for, when it came from the
    /// selected mode. Windows' MapTileViewModel.Option: a click on the tile makes
    /// it the mode's map, and the mode is what remembers the choice.</summary>
    public MapOption? Option { get; }

    public MapTileVM(string stem, string displayName, IImage? art = null)
    {
        Stem = stem;
        DisplayName = displayName;
        Art = art;
    }

    public MapTileVM(string stem, string displayName, MapOption? option, IImage? art = null)
    {
        Stem = stem;
        DisplayName = displayName;
        Option = option;
        Art = art;
    }
}

/// <summary>Server browser row. A port of the Windows ServerRowViewModel: same
/// derived labels, same favourites identity, same join gating, but the brushes
/// are resolved here so the template stays declarative (the Windows DataTriggers
/// have no Avalonia equivalent).</summary>
public partial class ServerRowVM : ObservableObject
{
    public ServerListing Listing { get; }
    public string Name { get; }
    public string MapLabel { get; }
    public string PlaylistLabel { get; }
    public string PlayersLabel { get; }
    public string AddressLabel { get; }
    public bool HasPassword { get; }
    public bool CanJoin { get; }
    public string JoinHint { get; }

    /// <summary>Stable identity for favourites; the master server has no server id.</summary>
    public string Key => Listing.Ip + ":" + Listing.Port;

    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isSteering;

    public ServerRowVM(ServerListing listing, string mapLabel, string playlistLabel)
    {
        Listing = listing;
        Name = string.IsNullOrWhiteSpace(listing.Name) ? Loc.Get("unnamed") : listing.Name;
        MapLabel = string.IsNullOrWhiteSpace(mapLabel) ? listing.Map : mapLabel;
        PlaylistLabel = string.IsNullOrWhiteSpace(playlistLabel) ? listing.Playlist : playlistLabel;
        PlayersLabel = listing.MaxPlayers > 0
            ? $"{listing.NumPlayers}/{listing.MaxPlayers}"
            : listing.NumPlayers.ToString();
        AddressLabel = ConnectTarget.Format(listing.Ip, listing.Port);
        CanJoin = listing.CanJoin;
        HasPassword = listing.HasPassword;
        JoinHint = listing.HasPassword
            ? Loc.Get("join_hint_password")
            : listing.CanJoin
                ? Loc.Format("join_hint_ok", listing.Ip + ":" + listing.Port)
                : Loc.Get("join_hint_no");
    }

    public string Detail => $"{MapLabel}  ·  {PlaylistLabel}  ·  {PlayersLabel}  ·  {AddressLabel}";
    public string JoinCaption => CanJoin ? Loc.Get("join") : Loc.Get("join_dash");

    /// <summary>A switch is settling, so no row takes a click until it lands.</summary>
    public bool JoinEnabled => CanJoin && !IsSteering;

    partial void OnIsSteeringChanged(bool value) => OnPropertyChanged(nameof(JoinEnabled));
    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(StarForeground));
    }

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public IBrush StarForeground => IsFavorite ? ThemeBrushes.AccentBright : ThemeBrushes.TextMuted;

    public bool HasModRequirement =>
        Listing.RequiredMods.Count > 0
        || Listing.AllowedMods.Count > 0
        || !string.IsNullOrWhiteSpace(Listing.ModsProfile);

    public string ModBadge
    {
        get
        {
            var n = Listing.RequiredMods.Count;
            if (n == 1)
                return Loc.Get("mods_req_one");
            if (n > 1)
                return Loc.Format("mods_req_n", n);
            if (Listing.AllowedMods.Count > 0)
                return Loc.Get("mods_allowlist");
            if (!string.IsNullOrWhiteSpace(Listing.ModsProfile))
                return Loc.Get("mods_profile_badge");
            return string.Empty;
        }
    }

    public string ModTooltip
    {
        get
        {
            var parts = new List<string>();
            if (Listing.RequiredMods.Count > 0)
                parts.Add(Loc.Get("mods_req_tip") + " " + string.Join(", ", Listing.RequiredMods));
            if (Listing.AllowedMods.Count > 0)
                parts.Add(Loc.Get("mods_allowlist") + ": " + string.Join(", ", Listing.AllowedMods));
            if (!string.IsNullOrWhiteSpace(Listing.ModsProfile))
                parts.Add(Loc.Get("mods_profile") + ": " + Listing.ModsProfile);
            return string.Join("\n", parts);
        }
    }
}

/// <summary>Installed-mods row (same shape as the Windows ModRowViewModel).</summary>
public partial class ModRowVM : ObservableObject
{
    public string Name { get; }
    public string Detail { get; }
    public IImage? IconSource { get; }
    public bool HasIcon => IconSource is not null;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _actionsEnabled = true;
    [ObservableProperty] private string _updateLabel = "";
    [ObservableProperty] private string _warningLabel = "";
    [ObservableProperty] private string _progressText = "";

    public bool HasUpdate => UpdateLabel.Length > 0;
    public bool HasWarning => WarningLabel.Length > 0;
    public bool HasProgress => ProgressText.Length > 0;

    partial void OnUpdateLabelChanged(string value) => OnPropertyChanged(nameof(HasUpdate));
    partial void OnWarningLabelChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnProgressTextChanged(string value) => OnPropertyChanged(nameof(HasProgress));

    public ModRowVM(string name, string detail, IImage? icon = null, bool enabled = true)
    {
        Name = name;
        Detail = detail;
        IconSource = icon;
        _enabled = enabled;
    }

    public string InstallCaption => "Install";
    public bool InstallEnabled => true;
}

/// <summary>Notes tab rows, ported from the Windows PatchNoteLine/Entry.</summary>
public sealed class PatchNoteLineVM
{
    /// <summary>NOTES.json marks a category with this prefix; anything else is a
    /// bullet. Windows parity: PatchNoteLine.HeaderPrefix.</summary>
    public const string HeaderPrefix = "## ";

    public required string Text { get; init; }
    public required bool IsHeader { get; init; }

    public static PatchNoteLineVM Parse(string? raw)
    {
        var s = raw ?? string.Empty;
        var header = s.StartsWith(HeaderPrefix, StringComparison.Ordinal);
        return new PatchNoteLineVM
        {
            Text = header ? s[HeaderPrefix.Length..].Trim() : s,
            IsHeader = header,
        };
    }
}

public sealed class PatchNoteEntryVM
{
    public required string Date { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<PatchNoteLineVM> Items { get; init; } = [];
}

/// <summary>Credits tab rows (ported from Windows CreditsCatalog entries).</summary>
public sealed class CreditEntryVM
{
    public required string Title { get; init; }
    public required string By { get; init; }
    public required string Blurb { get; init; }
    public required string Url { get; init; }

    public string UrlHost
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return Url;
            return uri.Host + uri.AbsolutePath.TrimEnd('/');
        }
    }

    public string UrlShort
    {
        get
        {
            var host = UrlHost;
            const string gh = "github.com/";
            return host.StartsWith(gh, StringComparison.OrdinalIgnoreCase)
                ? host[gh.Length..]
                : host;
        }
    }
}

/// <summary>Leaderboard board row (same columns as the Windows template, and the
/// same formatting rules as the Windows LeaderboardRowViewModel).</summary>
public sealed partial class LeaderboardRowVM : ObservableObject
{
    public LeaderboardRowVM(StatsPlayer player)
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
        AccLabel = FormatAccuracy(player.Hits, player.Shots);
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

    /// <summary>Set by the view model when this row's profile is open. Unlike
    /// Windows (which flips a DataTrigger) the row template reads the resolved
    /// brushes, so a selection repaints the row without a style trigger.</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(PersonaForeground));
    }

    /// <summary>Resolved from IsPodium so the row template stays declarative
    /// (replaces the Windows DataTriggers on the rank column).</summary>
    public IBrush RankForeground => IsPodium ? ThemeBrushes.AccentBright : ThemeBrushes.TextMuted;
    public FontWeight RankWeight => IsPodium ? FontWeight.Bold : FontWeight.Normal;
    public IBrush RowBackground => IsSelected ? ThemeBrushes.AccentDim : ThemeBrushes.SurfaceBg;
    public IBrush PersonaForeground => IsSelected ? ThemeBrushes.AccentBright : ThemeBrushes.TextPrimary;

    public static string FormatRate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            return "--";
        var pct = value <= 1.0 ? value * 100.0 : value;
        return pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    public static string FormatAccuracy(int hits, int shots)
    {
        if (shots <= 0)
            return "--";
        return FormatRate((double)hits / shots);
    }

    public static string FormatInput(string? raw)
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

    public static string FormatWeapon(string? raw)
    {
        var s = Dash(raw);
        if (s == "--")
            return s;
        if (s.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("na", StringComparison.OrdinalIgnoreCase))
            return "--";
        if (s.StartsWith("mp_weapon_", StringComparison.OrdinalIgnoreCase))
            s = s[10..];
        return s.Replace('_', ' ');
    }

    public static string FormatDuration(int secs)
    {
        if (secs <= 0)
            return "--";
        var m = secs / 60;
        var s = secs % 60;
        return m.ToString(CultureInfo.InvariantCulture) + ":" +
               s.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string Dash(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "--" : raw.Trim();
}

/// <summary>One file in flight, as the install card's list shows it (upstream's
/// TransferRow, built from the same <c>ActiveTransfer</c> snapshot). The engine's
/// type carries a path and two byte counts; this is the readable form of that.</summary>
public sealed class TransferRowVM
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public double Percent { get; init; }
}

/// <summary>One map or weapon split line in the player card.</summary>
public sealed class SplitRowVM
{
    public SplitRowVM(string label, string extra)
    {
        Label = label;
        Extra = extra;
    }

    public string Label { get; }
    public string Extra { get; }
}

/// <summary>One recent match in the player card. Clicking it opens the match.</summary>
public sealed class RecentMatchVM
{
    public RecentMatchVM(StatsMatch match, IReadOnlyDictionary<string, string>? mapNames)
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
            : MapLabels.PlayerName(match.Map, mapNames);
        KdLabel = match.Kills.ToString(CultureInfo.InvariantCulture) +
                  "-" +
                  match.Deaths.ToString(CultureInfo.InvariantCulture);
        DamageLabel = match.Damage.ToString("N0", CultureInfo.InvariantCulture);
        WeaponLabel = LeaderboardRowVM.FormatWeapon(match.Weapon);
        DurationLabel = LeaderboardRowVM.FormatDuration(match.Duration);
        HostLabel = LeaderboardRowVM.Dash(match.HostName);
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

    /// <summary>Result colour, resolved here rather than by a DataTrigger.</summary>
    public IBrush ResultForeground => IsWin
        ? ThemeBrushes.Accent
        : IsLoss ? ThemeBrushes.Danger : ThemeBrushes.TextSecondary;

    internal static string FormatAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t))
            return t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return raw.Trim();
    }
}

/// <summary>One participant in the match card.</summary>
public sealed class MatchPlayerVM
{
    public MatchPlayerVM(StatsMatchPlayer player, bool isWinner)
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
        AccLabel = LeaderboardRowVM.FormatAccuracy(player.Hits, player.Shots);
        WeaponLabel = LeaderboardRowVM.FormatWeapon(player.Weapon);
        InputLabel = LeaderboardRowVM.FormatInput(player.Input);
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

    public IBrush ResultForeground => IsWin
        ? ThemeBrushes.Accent
        : IsLoss ? ThemeBrushes.Danger : ThemeBrushes.TextSecondary;
}

/// <summary>A grouped 1v1 session: the history row and the match card.</summary>
public sealed class MatchSessionVM
{
    public MatchSessionVM(StatsMatchSession session, IReadOnlyDictionary<string, string>? mapNames)
    {
        Session = session;
        MatchId = StatsClient.SanitizeMatchId(session.MatchId);
        MapLabel = string.IsNullOrWhiteSpace(session.Map)
            ? "--"
            : MapLabels.PlayerName(session.Map, mapNames);
        PlaylistLabel = LeaderboardRowVM.Dash(session.Playlist);
        HostLabel = LeaderboardRowVM.Dash(session.HostName);
        DurationLabel = LeaderboardRowVM.FormatDuration(session.Duration);
        AtLabel = RecentMatchVM.FormatAt(session.At);
        KillsLabel = session.Kills.ToString(CultureInfo.InvariantCulture);
        DamageLabel = session.Damage.ToString("N0", CultureInfo.InvariantCulture);

        var players = new List<MatchPlayerVM>(session.Players.Length);
        foreach (var p in session.Players)
            players.Add(new MatchPlayerVM(p, session.WinnerId == p.AccountId));
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
            2 => players[0].Persona + "  " + Loc.Get("vs") + " " + players[1].Persona,
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
    public IReadOnlyList<MatchPlayerVM> Players { get; }
}

/// <summary>Season picker row. Id is what goes on the wire ("all" for the
/// all-seasons pseudo-season); Label carries the active marker.
/// Same shape as the Windows LbSeasonItem.</summary>
public sealed class LbSeasonVM
{
    public LbSeasonVM(string id, string name, bool active, bool isAll)
    {
        Id = id;
        IsAll = isAll;
        var label = isAll ? Loc.Get("lb_all_seasons") : name;
        if (active)
            label += " *";
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }
    public bool IsAll { get; }

    public override string ToString() => Label;
}

/// <summary>UI language picker row.</summary>
public sealed class LanguageRow
{
    public string Code { get; }
    public string Label { get; }
    public LanguageRow(string code, string label) { Code = code; Label = label; }
    public override string ToString() => Label;
}

/// <summary>Shared theme brushes for VM-side selection styling.</summary>
public static class ThemeBrushes
{
    public static readonly IBrush Accent = Get("#3DDA8A");
    public static readonly IBrush AccentBright = Get("#9FF0C0");
    public static readonly IBrush AccentDim = Get("#15291D");
    public static readonly IBrush AccentTrack = Get("#1E3B2A");
    public static readonly IBrush Danger = Get("#D95060");
    public static readonly IBrush TextPrimary = Get("#E9F1EC");
    public static readonly IBrush TextSecondary = Get("#A9B8AF");
    public static readonly IBrush TextMuted = Get("#71827A");
    public static readonly IBrush SurfaceBg = Get("#111815");
    public static readonly IBrush Transparent = new ImmutableSolidColorBrush(Avalonia.Media.Colors.Transparent);

    static IBrush Get(string hex)
    {
        if (Avalonia.Media.Color.TryParse(hex, out var c))
            return new ImmutableSolidColorBrush(c);
        return Transparent;
    }
}