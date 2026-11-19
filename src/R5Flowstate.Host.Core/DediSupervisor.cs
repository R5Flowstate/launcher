using System.Diagnostics;
using R5Flowstate.Content;
using R5Flowstate.Spawn;

namespace R5Flowstate.Host;

public enum DediEventKind
{
    Starting,
    Line,
    Ready,
    Fatal,
    Exited,
    GaveUp,
}

public sealed record DediEvent(string Instance, DediEventKind Kind, string Text);

/// <summary>
/// Runs one dedi instance until stopped: restarts after a crash with a growing
/// delay, and gives up when it keeps dying within minutes of starting.
/// </summary>
public sealed class DediSupervisor : IDisposable
{
    public const int MaxQuickCrashes = 5;
    static readonly TimeSpan QuickCrashWindow = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(20);
    static readonly TimeSpan ModListHold = TimeSpan.FromSeconds(20);
    static readonly TimeSpan ModListMinHold = TimeSpan.FromSeconds(3);

    // One mods/*.vdf set per install; a server must have read its own before the next is written.
    static readonly SemaphoreSlim StartGate = new(1, 1);

    readonly string _installPath;
    readonly HostConfig _config;
    readonly HostInstance _instance;
    readonly object _gate = new();
    Process? _proc;
    HostedConsoleTap? _tap;
    StreamWriter? _log;
    bool _stopping;
    TaskCompletionSource? _booted;

    public event Action<DediEvent>? Event;

    public DediSupervisor(string installPath, HostConfig config, HostInstance instance)
    {
        _installPath = installPath;
        _config = config;
        _instance = instance;
    }

    public string Name => _instance.Name;

    public bool IsRunning
    {
        get { lock (_gate) return _proc is { HasExited: false }; }
    }

    public static string LogPath(string installPath, string instance) =>
        Path.Combine(HostConfig.HostDir(installPath), "logs", instance + ".log");

    static readonly string[] SecretConVars = { "+sv_password", "+sv_rcon_password", "+fs_stats_host_key", "+rcon_key" };

    /// <summary>The launch line with secret values masked, for consoles and logs.</summary>
    public static string Describe(IReadOnlyList<string> args)
    {
        var shown = args.ToList();
        for (var i = 0; i + 1 < shown.Count; i++)
        {
            if (SecretConVars.Contains(shown[i], StringComparer.OrdinalIgnoreCase))
                shown[i + 1] = "<hidden>";
        }
        return LaunchArgs.FormatArgumentsOnly(shown);
    }

    public IReadOnlyList<string> BuildArgs()
    {
        var secrets = InstanceCfg.Read(_installPath, _instance);
        var overrides = _instance.PlaylistOverrides
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .ToList();
        var slots = 0;
        try
        {
            var settings = SlotCatalog.Settings(_installPath, _instance.Playlist);
            if (HostCapacity.Conflict(settings, _instance.PlaylistOverrides) is not null)
                overrides.RemoveAll(kv => HostCapacity.IsSlotVar(kv.Key));
            slots = HostCapacity.SlotCount(settings, _instance.PlaylistOverrides);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var args = LaunchArgs.BuildDediArgs(LaunchProfile.ShippingPlayer, new DediArgOptions
        {
            Port = _instance.Port,
            Password = secrets.ServerPassword,
            LaunchPlaylist = _instance.Playlist,
            Map = _instance.Map,
            Visibility = _instance.Visibility,
            PlaylistOverrides = overrides,
            MaxPlayers = slots,
            ExtraTokens = LaunchArgs.SplitExtraTokens(_instance.ExtraArgs),
            TickRate = TickRates.Normalize(_instance.TickRate, TickRates.Stock),
        }).ToList();

        // The engine runs its shipped cfgs (hostname "R5F Server", empty RCON password) after
        // anything exec'd from the command line; only "+convar value" tokens are re-applied over them.
        var convars = new List<string>();
        foreach (var (name, value) in new[]
                 {
                     ("+hostname", _instance.Hostname),
                     ("+hostdesc", _instance.Description),
                     ("+sv_rcon_password", secrets.RconPassword),
                     ("+fs_stats_host_key", secrets.StatsHostKey),
                 })
        {
            if (InstanceCfg.IsSafeValue(value, name == "+hostdesc" ? 256 : 128))
            {
                convars.Add(name);
                convars.Add(value!);
            }
        }
        var map = args.FindIndex(a => string.Equals(a, "+map", StringComparison.OrdinalIgnoreCase));
        args.InsertRange(map < 0 ? args.Count : map, convars);
        return args;
    }

    public async Task RunAsync(CancellationToken cancel)
    {
        var crashes = new Queue<DateTimeOffset>();
        var backoff = TimeSpan.FromSeconds(5);
        while (!cancel.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            int exitCode;
            try
            {
                exitCode = await RunOnceAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancel.IsCancellationRequested || IsStopping)
                break;
            Raise(DediEventKind.Exited, "exit code " + exitCode);
            if (!_instance.AutoRestart)
                break;

            var now = DateTimeOffset.UtcNow;
            if (now - started > QuickCrashWindow)
            {
                crashes.Clear();
                backoff = TimeSpan.FromSeconds(5);
            }
            crashes.Enqueue(now);
            while (crashes.Count > 0 && now - crashes.Peek() > QuickCrashWindow)
                crashes.Dequeue();
            if (crashes.Count >= MaxQuickCrashes)
            {
                Raise(DediEventKind.GaveUp,
                    crashes.Count + " exits within " + QuickCrashWindow.TotalMinutes + " minutes; not restarting");
                break;
            }

            try
            {
                await Task.Delay(backoff, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    async Task<int> RunOnceAsync(CancellationToken cancel)
    {
        var exe = Path.Combine(_installPath, ProcessSpawner.DediExeName);
        if (!File.Exists(exe))
            throw new FileNotFoundException("Not installed: " + exe);

        OpenLog();
        var args = BuildArgs();

        await StartGate.WaitAsync(cancel).ConfigureAwait(false);
        Process proc;
        try
        {
            var plan = InstanceMods.Apply(_installPath, _instance);
            Raise(DediEventKind.Starting, Describe(args));
            Raise(DediEventKind.Line, "[host] mods: " + (plan.Enabled.Count == 0 ? "none" : string.Join(", ",
                plan.Enabled.Select(id => plan.Required.Contains(id) ? id : id + " (optional)"))));
            if (LaunchArgs.SplitExtraTokens(_instance.ExtraArgs).Any(LaunchArgs.IsUnsignedPluginFlag))
                Raise(DediEventKind.Line, "[host] extra args: unsigned plugin flags are not passed to hosted servers");
            if (plan.Revoked.Count > 0)
                Raise(DediEventKind.Line, "[host] mods removed by review, not loaded: " + string.Join(", ", plan.Revoked));
            var booted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _booted = booted;
            proc = Spawn(exe, args);
            var exited = proc.WaitForExitAsync(cancel);
            await Task.WhenAll(
                    Task.WhenAny(booted.Task, exited, Task.Delay(ModListHold, cancel)),
                    Task.WhenAny(exited, Task.Delay(ModListMinHold, cancel)))
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _booted = null;
            StartGate.Release();
        }

        lock (_gate) _proc = proc;
        DediProcesses.WritePid(_installPath, _instance.Name, proc.Id);
        try
        {
            await proc.WaitForExitAsync(cancel).ConfigureAwait(false);
            return proc.ExitCode;
        }
        finally
        {
            DediProcesses.ClearPid(_installPath, _instance.Name);
            lock (_gate)
            {
                _tap?.Dispose();
                _tap = null;
                _proc = null;
            }
            proc.Dispose();
        }
    }

    Process Spawn(string exe, IReadOnlyList<string> args)
    {
        Process proc;
        if (OperatingSystem.IsWindows())
        {
            var tap = HostedConsoleTap.Create(LaunchRole.Dedicated);
            tap.LineReceived += OnLine;
            tap.Start();
            var result = ProcessSpawner.SpawnProcess(
                exe, args, _installPath, setFromR5fLauncher: true, createNoWindow: true,
                trackAs: LaunchRole.Dedicated, extraEnv: tap.ToEnvironment());
            if (!result.Ok || result.Process is null)
            {
                tap.Dispose();
                throw new InvalidOperationException("Could not start the dedicated server: " + result.Error);
            }
            proc = result.Process;
            lock (_gate) _tap = tap;
        }
        else
        {
            proc = StartUnderWine(exe, args);
        }
        lock (_gate) _proc = proc;
        return proc;
    }

    Process StartUnderWine(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(_config.Wine) ? "wine" : _config.Wine!,
            WorkingDirectory = _installPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.ArgumentList.Add(exe);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["VPROJECT"] = "1";
        psi.Environment["FROM_R5F_LAUNCHER"] = "1";
        psi.Environment["WINEDEBUG"] = "-all";

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start wine.");
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OnLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OnLine(e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    /// <summary>Console command to this instance (say, changelevel, quit).</summary>
    public bool SendCommand(string line)
    {
        lock (_gate)
        {
            if (_tap is not null)
                return _tap.TryWriteCommand(line);
            if (_proc is { HasExited: false } p && p.StartInfo.RedirectStandardInput)
            {
                var one = line.Split('\r', '\n')[0].Trim();
                if (one.Length is 0 or > 512)
                    return false;
                p.StandardInput.WriteLine(one);
                p.StandardInput.Flush();
                return true;
            }
        }
        return false;
    }

    /// <summary>quit, then kill if the process is still there after the grace.</summary>
    public async Task StopAsync()
    {
        Process? p;
        lock (_gate)
        {
            _stopping = true;
            p = _proc;
        }
        if (p is null)
            return;
        SendCommand("quit");
        using var grace = new CancellationTokenSource(StopGrace);
        try
        {
            await p.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public void ResumeAfterStop()
    {
        lock (_gate) _stopping = false;
    }

    void OnLine(string raw)
    {
        var line = HostedConsoleTap.StripAnsi(raw);
        lock (_gate)
        {
            try
            {
                _log?.WriteLine(line);
            }
            catch (IOException)
            {
            }
        }
        Raise(DediEventKind.Line, line);
        // The mod lists are read during engine init, long before playlists load.
        if (line.Contains("playlist", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate) _booted?.TrySetResult();
        }
        if (HostReadyGate.IsReadyLine(line, _instance.Map))
            Raise(DediEventKind.Ready, line);
        else if (HostReadyGate.IsFatalLine(line))
            Raise(DediEventKind.Fatal, line);
    }

    void OpenLog()
    {
        var path = LogPath(_installPath, _instance.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length > 32L * 1024 * 1024)
            File.Move(path, path + ".1", overwrite: true);
        lock (_gate)
        {
            _log?.Dispose();
            _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
    }

    void Raise(DediEventKind kind, string text) => Event?.Invoke(new DediEvent(_instance.Name, kind, text));

    public void Dispose()
    {
        lock (_gate)
        {
            _tap?.Dispose();
            _log?.Dispose();
            _log = null;
        }
    }
}

static class SlotCatalog
{
    static readonly object Gate = new();
    static string _install = string.Empty;
    static string _file = string.Empty;
    static DateTime _playlistWrite;
    static long _modsWrite;
    static PlaylistCatalog _catalog = new();

    public static IReadOnlyList<PlaylistSetting> Settings(string install, string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist))
            return Array.Empty<PlaylistSetting>();
        return Get(install).Find(playlist.Trim())?.Settings ?? Array.Empty<PlaylistSetting>();
    }

    static PlaylistCatalog Get(string install)
    {
        var file = PlaylistCatalogLoader.FindPlaylistFile(install) ?? string.Empty;
        var playlistWrite = DateTime.MinValue;
        long modsWrite = 0;
        try
        {
            if (file.Length > 0)
                playlistWrite = File.GetLastWriteTimeUtc(file);
            var modsDir = ModsStore.ModsDirectory(install);
            if (Directory.Exists(modsDir))
                modsWrite = Directory.GetLastWriteTimeUtc(modsDir).Ticks;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        lock (Gate)
        {
            if (_install == install && _file == file && _playlistWrite == playlistWrite &&
                _modsWrite == modsWrite && _catalog.Entries.Count > 0)
                return _catalog;

            IReadOnlyList<ModPlaylistSource>? mods = null;
            try
            {
                mods = ModsStore.Discover(install)
                    .Where(m => m.Maps.Count > 0)
                    .Select(m => new ModPlaylistSource
                    {
                        Id = m.Id,
                        Folder = Path.Combine(ModsStore.ModsDirectory(install), m.FolderName),
                        Maps = m.Maps,
                    })
                    .ToList();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _catalog = PlaylistCatalogLoader.Load(install, "english", uiLoc: null, mods: mods);
            _install = install;
            _file = file;
            _playlistWrite = playlistWrite;
            _modsWrite = modsWrite;
            return _catalog;
        }
    }
}
