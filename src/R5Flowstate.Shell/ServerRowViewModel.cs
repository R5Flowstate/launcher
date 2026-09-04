using System.ComponentModel;
using System.Runtime.CompilerServices;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public sealed class ServerRowViewModel : INotifyPropertyChanged
{
    private int _pingMs = -1;
    private bool _isFavorite;
    private bool _isSteering;

    public ServerRowViewModel(ServerListing listing, string mapLabel, string playlistLabel)
    {
        Listing = listing;
        Name = string.IsNullOrWhiteSpace(listing.Name) ? Loc.Get("unnamed") : listing.Name;
        MapLabel = string.IsNullOrWhiteSpace(mapLabel) ? listing.Map : mapLabel;
        PlaylistLabel = string.IsNullOrWhiteSpace(playlistLabel) ? listing.Playlist : playlistLabel;
        PlayersLabel = listing.MaxPlayers > 0
            ? $"{listing.NumPlayers}/{listing.MaxPlayers}"
            : listing.NumPlayers.ToString();
        AddressLabel = LaunchArgsSafe(listing.Ip, listing.Port);
        CanJoin = listing.CanJoin;
        HasPassword = listing.HasPassword;
        JoinHint = listing.HasPassword
            ? Loc.Get("join_hint_password")
            : listing.CanJoin
                ? Loc.Format("join_hint_ok", listing.Ip + ":" + listing.Port)
                : Loc.Get("join_hint_no");
    }

    public ServerListing Listing { get; }
    public string Name { get; }
    public string MapLabel { get; }
    public string PlaylistLabel { get; }
    public string PlayersLabel { get; }
    public string AddressLabel { get; }
    public bool HasPassword { get; }
    public string Detail =>
        $"{MapLabel}  ·  {PlaylistLabel}  ·  {PlayersLabel}  ·  {AddressLabel}";
    public bool CanJoin { get; }
    public string JoinHint { get; }
    public string JoinCaption => CanJoin ? Loc.Get("join") : Loc.Get("join_dash");

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

    /// <summary>A switch is settling, so no row will take a click until it lands.</summary>
    public bool IsSteering
    {
        get => _isSteering;
        set
        {
            if (_isSteering == value)
                return;
            _isSteering = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(JoinEnabled));
        }
    }

    public bool JoinEnabled => CanJoin && !_isSteering;

    /// <summary>Stable identity for favourites; the master server has no server id.</summary>
    public string Key => Listing.Ip + ":" + Listing.Port;

    /// <summary>Round-trip in ms; -1 while unmeasured, 0 when the host did not answer.</summary>
    public int PingMs
    {
        get => _pingMs;
        set
        {
            if (_pingMs == value)
                return;
            _pingMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PingLabel));
            OnPropertyChanged(nameof(PingTier));
        }
    }

    public string PingLabel => _pingMs switch
    {
        < 0 => "--",
        0 => Loc.Get("n_a"),
        _ => _pingMs + " ms",
    };

    /// <summary>Drives the colour band on the row; kept out of the view's way.</summary>
    public string PingTier => _pingMs switch
    {
        < 0 or 0 => "None",
        <= 80 => "Good",
        <= 160 => "Fair",
        _ => "Poor",
    };

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    public string FavoriteGlyph => _isFavorite ? "*" : "+";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string LaunchArgsSafe(string ip, int port)
    {
        try { return R5Flowstate.Spawn.LaunchArgs.FormatConnectTarget(ip, port); }
        catch { return ip + ":" + port; }
    }
}
