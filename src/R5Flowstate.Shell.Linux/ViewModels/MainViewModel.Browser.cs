using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>
/// The public server browser: the same master-server call, the same row shape,
/// the same ordering, the same favourites identity and the same status line as
/// the Windows launcher (MainWindow.Browser.cs). The transport moved out to
/// <see cref="MasterServerClient"/>; nothing else about the lane changed.
///
/// Two Windows pieces are deliberately absent, both for the same reason — they
/// live in the net8.0-windows <c>R5Flowstate.Spawn</c> project:
/// <list type="bullet">
/// <item>the map/playlist catalog, so a row shows the raw ids. Windows shows the
/// raw ids too until its catalog loads.</item>
/// <item>the console tap, so a join always launches the client instead of
/// steering a running one in place. <c>IsSteering</c> is carried on the row for
/// when that lands.</item>
/// </list>
/// </summary>
public partial class MainViewModel
{
    /// <summary>The play host is fixed (ProductConstants); there is nothing for a
    /// player to point elsewhere, so this is not a setting.</summary>
    public static string MasterServerUrl =>
        MasterServerClient.NormalizeBaseUrl(NoticeClient.DefaultMasterServerUrl);

    /// <summary>True while a list request is in flight, so the UI can show it.</summary>
    [ObservableProperty] private bool _browserBusy;

    /// <summary>
    /// The window owns the legal-notice version (it drives the dialog), so it
    /// hands the gate in as a callback rather than the VM keeping a second copy
    /// of "accepted". Null means "no gate" — the offline probes and the self-test
    /// run that way.
    /// </summary>
    public Func<bool>? ServersUnlockedProbe { get; set; }

    /// <summary>
    /// Windows' unlock rule for the master-server tabs, as a function of the two
    /// numbers it is made of: the version this session knows is current, and the
    /// one the player accepted. Spelled out here rather than left inside the
    /// window so the suite can state the rule itself — as a property of the
    /// numbers, not of whatever happens to be in the player's settings file.
    ///
    /// <paramref name="current"/> is 0 until the notice has been fetched once:
    /// then any acceptance counts, and a later server-side bump is picked up on
    /// the next launcher start.
    /// </summary>
    public static bool ServersUnlockedFor(int accepted, int current)
        => current > 0 ? accepted >= current : accepted > 0;

    readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _browserCts;

    /// <summary>Favourites as starred last session. Called once from the ctor.</summary>
    void InitBrowser()
    {
        foreach (var key in LinuxSettings.ParseFavorites(_s.FavoriteServers))
            _favorites.Add(key);
    }

    /// <summary>Refresh button / tab entry. Same fetch, with the "Refreshing…" line.</summary>
    [RelayCommand]
    private Task RefreshServersAsync() => RefreshServerListAsync(quiet: false);

    /// <summary>
    /// The 12 s poll tick. Quiet, so a steady browser does not flicker its status
    /// line every tick; failures still surface, because a silent browser is worse
    /// than a stale one.
    /// </summary>
    public Task PollServersAsync() => RefreshServerListAsync(quiet: true);

    internal async Task RefreshServerListAsync(bool quiet)
    {
        // Defence in depth: the tab is only reachable through the gate, but a
        // poll tick must never list servers for a player who has not accepted
        // the notice this session.
        if (ServersUnlockedProbe is { } unlocked && !unlocked())
        {
            ClearServers();
            SetBrowserStatus(Loc.Get("status_eula_servers"));
            return;
        }

        _browserCts?.Cancel();
        var cts = new CancellationTokenSource();
        _browserCts = cts;
        var ct = cts.Token;

        if (!quiet)
            SetBrowserStatus(Loc.Get("lb_refreshing"));

        // The wire version is what the master server gates on: the install's own
        // stamp when there is one, else the channel's expected gate, else the
        // version this launcher was compiled against.
        var wire = VersionIdentity.ResolveExpectedWireVersion(InstallPath ?? "", _channel?.EffectiveGateName);
        var url = MasterServerUrl;

        if (!quiet)
            BrowserBusy = true;
        ServerListResult result;
        try
        {
            result = await MasterServerClient.ListServersAsync(url, wire, Loc.Code, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            BrowserBusy = false;
        }

        // A newer request has already replaced this one; its answer is the truth.
        if (ct.IsCancellationRequested)
            return;

        var rows = new List<ServerRowVM>();
        if (result.Success)
        {
            foreach (var listing in result.Servers)
                rows.Add(ToRow(listing));
        }

        PublishServerRows(rows);

        if (!result.Success)
        {
            SetBrowserStatus(result.Error ?? Loc.Get("browser_failed"));
            AppendLog("Server browser: " + (result.Error ?? "unknown error"));
            return;
        }

        if (result.UpdateRequired)
        {
            SetBrowserStatus(Loc.Get("browser_update_required"));
            AppendLog("Server browser: this build is not allowed on the master server.");
            return;
        }

        PaintBrowserCount();
        if (!quiet)
        {
            var players = 0;
            foreach (var row in rows)
                players += row.Listing.NumPlayers;
            AppendLog($"Server browser: {rows.Count} server(s), {players} player(s) listed.");
        }
    }

    /// <summary>
    /// One row per joinable listing. The friendly map/playlist names need the
    /// catalog (see the class comment), so both labels are the raw ids for now.
    /// </summary>
    internal ServerRowVM ToRow(ServerListing listing) =>
        new(listing, MapLabels.Label(listing.Map, CatalogMapNames), listing.Playlist);

    /// <summary>Stars every row the key set remembers. Static and side-effect free
    /// so the identity rule can be exercised without touching settings.json.</summary>
    internal static void ApplyFavorites(IEnumerable<ServerRowVM> rows, ICollection<string> favorites)
    {
        foreach (var row in rows)
            row.IsFavorite = favorites.Contains(row.Key);
    }

    /// <summary>Favourites first, then the fuller servers — an empty lobby is
    /// rarely the answer. Windows parity: SortServerRows.</summary>
    internal static void SortServerRows(List<ServerRowVM> rows)
    {
        var sorted = rows
            .OrderByDescending(r => r.IsFavorite)
            .ThenByDescending(r => r.Listing.NumPlayers)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        rows.Clear();
        rows.AddRange(sorted);
    }

    /// <summary>Stars, orders and shows a fresh list. The collection is
    /// observable, so one clear-then-fill repaints once and nothing is ever bound
    /// to a half-list.</summary>
    void PublishServerRows(List<ServerRowVM> rows)
    {
        ApplyFavorites(rows, _favorites);
        SortServerRows(rows);

        Servers.Clear();
        foreach (var row in rows)
            Servers.Add(row);
    }

    void ClearServers()
    {
        if (Servers.Count > 0)
            Servers.Clear();
    }

    /// <summary>
    /// The star rule, on a plain set: present → removed, absent → added, keyed
    /// case-insensitively on "ip:port". Static so a test can drive it on its own
    /// set (the command below is the only caller that also writes settings).
    /// </summary>
    internal static bool ToggleFavoriteKey(ICollection<string> favorites, string key)
    {
        if (favorites.Remove(key))
            return false;
        favorites.Add(key);
        return true;
    }

    /// <summary>The count line under the tabs: the only thing a browser status
    /// normally says. Windows parity: PaintBrowserCount.</summary>
    void PaintBrowserCount()
    {
        var n = Servers.Count;
        var players = 0;
        foreach (var row in Servers)
            players += row.Listing.NumPlayers;
        SetBrowserStatus(n == 0
            ? Loc.Get("browser_none")
            : Loc.Format("browser_count", n, players));
    }

    /// <summary>Both the simple tab's line and the advanced card's line read the
    /// same property, so one write paints both (Windows sets both TextBlocks).
    /// Deliberately not logged: the 12 s poll would otherwise fill the log.</summary>
    void SetBrowserStatus(string text) => BrowserStatus = text;

    /// <summary>
    /// Stars or unstars a row and writes the list to disk. The star is keyed by
    /// "ip:port" because the master server publishes no server id, and the
    /// re-sort puts a new favourite at the top immediately.
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(ServerRowVM? row)
    {
        if (row is null)
            return;

        row.IsFavorite = ToggleFavoriteKey(_favorites, row.Key);

        _s.FavoriteServers = LinuxSettings.FormatFavorites(_favorites);
        try { _s.Save(); }
        catch (Exception ex) { AppendLog("Settings save failed: " + ex.Message); }

        AppendLog(row.IsFavorite ? $"Favorited {row.Name} ({row.Key})" : $"Unfavorited {row.Name} ({row.Key})");

        // Re-order in place: favourites sort first, so the row the player just
        // starred moves under their cursor.
        var rows = Servers.ToList();
        PublishServerRows(rows);
    }

    /// <summary>
    /// Joins the server on a row. The address came off the master server, so it
    /// is validated as a literal host and port before it reaches a launch; a
    /// listing this launcher will not join is reported, never joined blindly.
    /// </summary>
    [RelayCommand]
    private void JoinServerRow(ServerRowVM? row)
    {
        if (row is null)
            return;

        if (!row.CanJoin)
        {
            SetBrowserStatus(Loc.Get("join_hint_no"));
            return;
        }

        if (!ConnectTarget.TryFormat(row.Listing.Ip, row.Listing.Port, out var target))
        {
            SetBrowserStatus(Loc.Get("status_malformed"));
            AppendLog($"Join: refusing a malformed address from the master server ('{row.Listing.Ip}:{row.Listing.Port}')");
            return;
        }

        // Windows records the listing it joined (_joinedServer) so the console
        // knows the server pane has nothing of ours behind it.
        JoinedRemote = true;
        JoinServer(target);
    }
}
