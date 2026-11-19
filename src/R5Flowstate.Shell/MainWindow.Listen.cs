using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>
/// Local play on one process: the game's own exe hosts the lobby and every local
/// match, so there is one window and one console. A separate server is only for
/// hosting online, a password, or the Advanced tab.
/// </summary>
public partial class MainWindow
{
    const string ListenHostMarker = "local_host_start";
    const string HostStartTag = "[LOCAL-HOST] start ";

    bool _listenSession;
    string _listenProbeKey = string.Empty;
    bool _listenProbeResult;

    /// <summary>A client older than this launcher cannot take a match request, so it keeps the separate server.</summary>
    async Task<bool> UseListenHostAsync(string root)
    {
        if (!_settings.SimpleMode || IsDediHostOnlineOn() || IsPasswordProtectOn())
            return false;
        if (!ProcessSpawner.PreflightRole(root, LaunchRole.Dedicated, false).Ok)
            return false;

        var dll = Path.Combine(root, GameBuilds.IsS30(root) ? "client_s30.dll" : "client.dll");
        var info = new FileInfo(dll);
        if (!info.Exists)
            return false;
        var key = dll + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        if (key == _listenProbeKey)
            return _listenProbeResult;

        var found = await Task.Run(() =>
            File.ReadAllBytes(dll).AsSpan().IndexOf(Encoding.ASCII.GetBytes(ListenHostMarker)) >= 0).ConfigureAwait(true);
        _listenProbeKey = key;
        _listenProbeResult = found;
        Log("Listen host " + (found ? "available in " : "not in ") + Path.GetFileName(dll));
        return found;
    }

    // Goes onto the client command line as its own token: letters, digits and '_' only.
    static bool IsPlainLaunchName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64
        && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    async Task StartListenSessionAsync(string root, string playlist, string map)
    {
        if (_listenSession && SteerListenSession(playlist, map))
            return;

        DisarmHostedMatch();
        var killed = await Task.Run(() => ProcessSpawner.KillAll(root)).ConfigureAwait(true);
        if (killed > 0)
            Log($"Play: killed {killed} prior process(es)");
        await Task.Run(ProcessSpawner.KillAllDediImages).ConfigureAwait(true);
        DropLocalRcon();
        ResetClientConsoleForNewSession();

        var args = BuildClientArgs(includeConnect: false).ToList();
        args.Add("-local_boot");
        args.Add("-local_tickrate");
        args.Add(_settings.TickRate.ToString(CultureInfo.InvariantCulture));
        if (_settings.Cheats)
        {
            args.Add("-local_cheats");
            args.Add("1");
        }
        if (!IsLobbyStem(map))
        {
            if (!IsPlainLaunchName(playlist) || !IsPlainLaunchName(map))
            {
                Log($"Local match refused: playlist or map name is not a plain identifier ({playlist} / {map}).");
                return;
            }
            args.Add("-local_playlist");
            args.Add(playlist);
            args.Add("-local_map");
            args.Add(map);
        }

        var result = SpawnRoleResult(LaunchRole.Client, args, quiet: true);
        if (!result.Ok)
        {
            SetSimpleStatus(Loc.Get("status_generic_error"));
            Log("Play failed: " + (result.Error ?? "client spawn failed"));
            return;
        }

        _listenSession = true;
        _livePlaylist = playlist;
        _liveMap = map;
        _lastClientPid = result.ProcessId;
        _replayClientPid = null;
        _localPlayClientPid = result.ProcessId;
        _localPlayClientModsStamp = ModListStamp(root);
        Log($"[Play] one process: {playlist} on {map}, client pid={result.ProcessId}");
        UpdateKillButtons();
        SetSimpleStatus(Loc.Get("status_playing"));
        MaybeOpenConsoleForLaunch();
    }

    bool SteerListenSession(string playlist, string map)
    {
        if (!ClientIsSteerable()
            || !LocalRcon.IsSafeIdentifier(playlist.ToLowerInvariant())
            || !LocalRcon.IsSafeIdentifier(map.ToLowerInvariant())
            || _consoleClient?.TryWriteCommand($"local_host_start {playlist} {map}") != true)
            return false;

        _livePlaylist = playlist;
        _liveMap = map;
        Log($"[Play] one process: switching to {playlist} on {map}");
        SetSimpleStatus(Loc.Format("status_changing_map", MapDisplayName(map)));
        RefreshSimplePlayButton();
        return true;
    }

    bool ListenChangePending()
    {
        var map = _selectedMode?.SelectedMapStem;
        var playlist = _selectedMode?.PlaylistId;
        if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(playlist))
            return false;
        if (IsLobbyStem(map))
            return !IsLobbyStem(_liveMap ?? string.Empty);
        return !string.Equals(playlist, _livePlaylist, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(map, _liveMap, StringComparison.OrdinalIgnoreCase);
    }

    void OnListenChangeMap()
    {
        var map = _selectedMode?.SelectedMapStem;
        var playlist = _selectedMode?.PlaylistId;
        if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(playlist) || !SteerListenSession(playlist, map))
            SetSimpleStatus(Loc.Get("status_change_map_pick"));
    }

    /// <summary>The match can also change from inside the game, and the server only takes settings once it is up.</summary>
    void NoteListenLine(string line)
    {
        if (!_listenSession)
            return;

        var at = line.IndexOf(HostStartTag, StringComparison.Ordinal);
        if (at >= 0)
        {
            var parts = line[(at + HostStartTag.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[1] == "on")
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _livePlaylist = parts[0];
                    _liveMap = parts[2];
                    RefreshSimplePlayButton();
                });
            }
        }
    }
}
