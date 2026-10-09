using System.Text.Json;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Host;
using R5Flowstate.Spawn;

namespace R5Flowstate.Host.Cli;

static class Program
{
    const string Usage = """
        r5f-host -- R5Flowstate dedicated server host

          install  --path DIR [--ring live|playtest]   first install (or adopt a folder)
          check                                        is an update available
          update   [--repair]                          bring files to the channel tip
          status   [--json]                            versions, instances, running pids
          ring     live|playtest                       switch ring (run update after)

          instance add NAME [options]                  new server instance
          instance set NAME [options]                  change one
          instance list [--json] | remove NAME | args NAME
            --port N --playlist P --map M --visibility public|hidden|offline
            --hostname TEXT --description TEXT --override VAR=VALUE --extra "ARGS"
            --password X|- --rcon-password X|- --stats-key X|-   ('-' reads stdin)
            --no-restart

          run   [NAME...] [--no-auto-update]           run instances in the foreground
          stop  [NAME]                                 stop running instances
          logs  NAME [--lines N]

          mods list | add PIN... [--optional] | remove OWNER-NAME
          mods update | sync | import PROFILE-CODE

        Every command takes --path DIR, or R5F_HOST_PATH, or runs in the install folder.
        """;

    static async Task<int> Main(string[] argv)
    {
        var a = new Args(argv);
        if (a.Positional.Count == 0 || a.Has("help") || a.Positional[0] is "help" or "-h")
        {
            Console.WriteLine(Usage);
            return a.Positional.Count == 0 ? 1 : 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return a.Positional[0] switch
            {
                "install" => await Install(a, cts.Token),
                "check" => await Check(a, cts.Token),
                "update" => await Update(a, cts.Token),
                "status" => Status(a),
                "ring" => Ring(a),
                "instance" => Instance(a),
                "run" => await Run(a, cts.Token),
                "stop" => Stop(a),
                "logs" => Logs(a),
                "mods" => await Mods(a, cts.Token),
                _ => Fail("unknown command '" + a.Positional[0] + "'\n\n" + Usage),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                       or UnauthorizedAccessException or HttpRequestException
                                       or FileNotFoundException or JsonException)
        {
            return Fail(ex.Message);
        }
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 2;
    }

    static string ResolvePath(Args a, bool mustExist = true)
    {
        var p = a.Get("path") ?? Environment.GetEnvironmentVariable("R5F_HOST_PATH");
        if (string.IsNullOrWhiteSpace(p))
        {
            var cwd = Environment.CurrentDirectory;
            if (File.Exists(HostConfig.ConfigPath(cwd)) || !mustExist)
                p = cwd;
            else
                throw new InvalidOperationException("No install here. Pass --path DIR or set R5F_HOST_PATH.");
        }
        var full = Path.GetFullPath(p!);
        if (mustExist && !File.Exists(HostConfig.ConfigPath(full)))
            throw new InvalidOperationException(full + " is not a host install. Run: r5f-host install --path " + full);
        return full;
    }

    static async Task<int> Install(Args a, CancellationToken cancel)
    {
        var path = Path.GetFullPath(a.Get("path") ?? throw new ArgumentException("install needs --path DIR"));
        if (InstallPathPolicy.IsForbidden(path, AppContext.BaseDirectory))
            return Fail(path + " cannot hold a server install (Program Files, the launcher folder, or AppData)");

        var cfg = HostConfig.Load(path);
        if (a.Get("ring") is { } r)
        {
            if (!HostRings.TryParse(r, out var ring))
                return Fail("ring must be live or playtest");
            cfg.Ring = ring;
        }
        if (a.Get("channel") is { } ch)
            cfg.ChannelUrl = ch;
        if (cfg.Instances.Count == 0)
            cfg.Instances.Add(new HostInstance { Name = "main" });
        Directory.CreateDirectory(path);
        cfg.Save(path);
        foreach (var inst in cfg.Instances)
        {
            if (!File.Exists(InstanceCfg.PathFor(path, inst)))
                InstanceCfg.Write(path, inst, new InstanceCfg.Secrets { RconPassword = InstanceCfg.NewRconPassword() });
        }
        Console.WriteLine("ring " + HostRings.Name(cfg.Ring) + "  channel " + cfg.EffectiveChannelUrl);
        return await DoUpdate(path, cfg, repair: false, cancel);
    }

    static async Task<int> Check(Args a, CancellationToken cancel)
    {
        var path = ResolvePath(a);
        var cfg = HostConfig.Load(path);
        using var fetcher = new FileSystemFetcher();
        var check = await HostInstaller.CheckAsync(path, cfg, fetcher, cancel);
        foreach (var t in check.Tracks)
        {
            Console.WriteLine($"{t.Track,-9} {t.Tip.CatalogVersion,-10} {(t.UpToDate ? "up to date" : "update")}");
        }
        Console.WriteLine("gate " + check.Channel.EffectiveGateName);
        return check.UpdateAvailable ? 10 : 0;
    }

    static async Task<int> Update(Args a, CancellationToken cancel)
    {
        var path = ResolvePath(a);
        return await DoUpdate(path, HostConfig.Load(path), a.Has("repair"), cancel);
    }

    static async Task<int> DoUpdate(string path, HostConfig cfg, bool repair, CancellationToken cancel)
    {
        using var fetcher = new FileSystemFetcher();
        var last = DateTime.MinValue;
        var progress = new Progress<ContentInstallProgress>(p =>
        {
            if (DateTime.UtcNow - last < TimeSpan.FromSeconds(2))
                return;
            last = DateTime.UtcNow;
            var msg = string.IsNullOrWhiteSpace(p.Message) ? p.Phase : p.Message;
            var (cur, tot) = p.JobTotal > 0 ? (p.JobCurrent, p.JobTotal) : (p.Current, p.Total);
            if (tot >= 100_000_000 && p.Unit == ProgressUnit.Bytes)
                msg = $"{p.Track} {msg} {cur / 1e9:0.00}/{tot / 1e9:0.00} GB ({100.0 * cur / tot:0}%)";
            else if (tot > 0)
                msg = $"{p.Track} {msg} {cur}/{tot}";
            if (!string.IsNullOrWhiteSpace(msg))
                Console.WriteLine("  " + msg);
        });
        var check = await HostInstaller.UpdateAsync(path, cfg, fetcher, progress, repair, cancel);
        Console.WriteLine("installed " + HostRunner.Describe(check));
        using var mods = new HostMods(path);
        await mods.SyncAsync(new Progress<string>(s => Console.WriteLine("  " + s)), cancel);
        return 0;
    }

    static int Status(Args a)
    {
        var path = ResolvePath(a);
        var cfg = HostConfig.Load(path);
        var state = HostState.Load(path);
        var pids = DediProcesses.LivePids(path);
        if (a.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                path,
                ring = HostRings.Name(cfg.Ring),
                gate = state.GateName,
                tracks = state.Tracks,
                instances = cfg.Instances.Select(i => new
                {
                    i.Name,
                    i.Port,
                    i.Playlist,
                    i.Map,
                    visibility = i.Visibility.ToString().ToLowerInvariant(),
                    pid = pids.TryGetValue(i.Name, out var pid) ? pid : (int?)null,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        Console.WriteLine("install  " + path);
        Console.WriteLine("ring     " + HostRings.Name(cfg.Ring) + "  gate " + (state.GateName ?? "-"));
        foreach (var (name, t) in state.Tracks)
            Console.WriteLine($"{name,-9}{t.CatalogVersion}");
        foreach (var i in cfg.Instances)
        {
            var run = pids.TryGetValue(i.Name, out var pid) ? "running pid " + pid : "stopped";
            Console.WriteLine($"  {i.Name,-12} port {i.Port,-6} {i.Visibility.ToString().ToLowerInvariant(),-8} {i.Playlist ?? "-",-20} {i.Map ?? "-",-24} {run}");
        }
        return 0;
    }

    static int Ring(Args a)
    {
        var path = ResolvePath(a);
        if (a.Positional.Count < 2 || !HostRings.TryParse(a.Positional[1], out var ring))
            return Fail("usage: r5f-host ring live|playtest");
        var cfg = HostConfig.Load(path);
        cfg.Ring = ring;
        cfg.Save(path);
        Console.WriteLine("ring " + HostRings.Name(ring) + "; run 'r5f-host update' to switch files");
        return 0;
    }

    static int Instance(Args a)
    {
        var path = ResolvePath(a);
        var cfg = HostConfig.Load(path);
        var verb = a.Positional.Count > 1 ? a.Positional[1] : "list";
        var name = a.Positional.Count > 2 ? a.Positional[2] : null;

        switch (verb)
        {
            case "list":
                if (a.Has("json"))
                    Console.WriteLine(JsonSerializer.Serialize(cfg.Instances, new JsonSerializerOptions { WriteIndented = true }));
                else
                    foreach (var i in cfg.Instances)
                        Console.WriteLine($"{i.Name,-12} port {i.Port,-6} {i.Visibility.ToString().ToLowerInvariant(),-8} {i.Playlist ?? "-"} {i.Map ?? "-"}");
                return 0;

            case "add":
            {
                if (!HostInstance.IsValidName(name))
                    return Fail("instance names are 1-24 of a-z 0-9 _ -");
                if (cfg.Find(name!) is not null)
                    return Fail(name + " already exists");
                if (cfg.Instances.Count >= HostConfig.MaxInstances)
                    return Fail("at most " + HostConfig.MaxInstances + " instances");
                var inst = new HostInstance
                {
                    Name = name!,
                    Port = NextFreePort(cfg),
                };
                Apply(a, path, cfg, inst, isNew: true);
                cfg.Instances.Add(inst);
                cfg.Save(path);
                Console.WriteLine("added " + inst.Name + " on port " + inst.Port);
                return 0;
            }

            case "set":
            {
                var inst = cfg.Find(name ?? "") ?? throw new InvalidOperationException("no instance " + name);
                Apply(a, path, cfg, inst, isNew: false);
                cfg.Save(path);
                Console.WriteLine("updated " + inst.Name + "; restart it to apply");
                return 0;
            }

            case "remove":
            {
                var inst = cfg.Find(name ?? "") ?? throw new InvalidOperationException("no instance " + name);
                if (DediProcesses.LivePids(path).ContainsKey(inst.Name))
                    return Fail(inst.Name + " is running; stop it first");
                cfg.Instances.Remove(inst);
                cfg.Save(path);
                var file = InstanceCfg.PathFor(path, inst);
                if (File.Exists(file))
                    File.Delete(file);
                Console.WriteLine("removed " + inst.Name);
                return 0;
            }

            case "args":
            {
                var inst = cfg.Find(name ?? "") ?? throw new InvalidOperationException("no instance " + name);
                using var s = new DediSupervisor(path, cfg, inst);
                Console.WriteLine(LaunchArgs.FormatArgumentsOnly(s.BuildArgs()));
                return 0;
            }
        }
        return Fail("usage: r5f-host instance list|add|set|remove|args NAME");
    }

    static void Apply(Args a, string path, HostConfig cfg, HostInstance inst, bool isNew)
    {
        if (a.Get("port") is { } port)
        {
            if (!int.TryParse(port, out var p) || p is < 1024 or > 65535)
                throw new ArgumentException("port must be 1024-65535");
            if (cfg.Instances.Any(o => o != inst && o.Port == p))
                throw new ArgumentException("port " + p + " is used by another instance");
            inst.Port = p;
        }
        if (a.Get("playlist") is { } pl)
            inst.Playlist = Identifier(pl, "playlist");
        if (a.Get("map") is { } map)
            inst.Map = Identifier(map, "map");
        if (a.Get("visibility") is { } vis)
        {
            inst.Visibility = vis.ToLowerInvariant() switch
            {
                "public" => SpireVisibility.Public,
                "hidden" => SpireVisibility.Hidden,
                "offline" => SpireVisibility.Offline,
                _ => throw new ArgumentException("visibility is public, hidden or offline"),
            };
        }
        if (a.Get("hostname") is { } hn)
            inst.Hostname = hn.Length == 0 ? null : hn;
        if (a.Get("description") is { } desc)
            inst.Description = desc.Length == 0 ? null : desc;
        if (a.Get("extra") is { } extra)
            inst.ExtraArgs = extra.Length == 0 ? null : extra;
        if (a.Has("no-restart"))
            inst.AutoRestart = false;
        foreach (var ov in a.All("override"))
        {
            var eq = ov.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException("--override wants VAR=VALUE");
            var k = ov[..eq].Trim();
            var v = ov[(eq + 1)..].Trim();
            if (v.Length == 0)
            {
                inst.PlaylistOverrides.Remove(k);
                continue;
            }
            if (!LaunchArgs.IsSafeOverrideName(k) || !LaunchArgs.IsSafeOverrideValue(v))
                throw new ArgumentException("override " + k + " has characters a playlist var cannot carry");
            inst.PlaylistOverrides[k] = v;
        }

        if (inst.PlaylistOverrides.Count > 0 && !string.IsNullOrEmpty(inst.Playlist))
        {
            var entry = PlaylistCatalogLoader.Load(path).Find(inst.Playlist);
            if (entry is not null)
            {
                foreach (var (k, v) in inst.PlaylistOverrides)
                {
                    var decl = entry.Settings.FirstOrDefault(s => s.Var == k)
                        ?? throw new ArgumentException(inst.Playlist + " has no server setting " + k
                            + (entry.Settings.Count == 0 ? "" : " (it has: " + string.Join(", ", entry.Settings.Select(s => s.Var)) + ")"));
                    if (!decl.Validate(v, out var reason))
                        throw new ArgumentException(k + ": " + reason);
                }
            }
        }

        var secrets = InstanceCfg.Read(path, inst);
        if (isNew && string.IsNullOrEmpty(secrets.RconPassword))
            secrets.RconPassword = InstanceCfg.NewRconPassword();
        if (a.Get("password") is { } pw)
            secrets.ServerPassword = Secret(pw);
        if (a.Get("rcon-password") is { } rp)
            secrets.RconPassword = Secret(rp);
        if (a.Get("stats-key") is { } sk)
            secrets.StatsHostKey = Secret(sk);
        InstanceCfg.Write(path, inst, secrets);
    }

    static string? Secret(string arg)
    {
        var v = arg == "-" ? Console.In.ReadLine() ?? string.Empty : arg;
        v = v.Trim();
        return v.Length == 0 ? null : v;
    }

    static string Identifier(string v, string what)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.Length == 0 || !LocalRcon.IsSafeIdentifier(v))
            throw new ArgumentException(what + " must be a plain name");
        return v;
    }

    static int NextFreePort(HostConfig cfg)
    {
        var p = LaunchArgs.DefaultDediPort;
        while (cfg.Instances.Any(i => i.Port == p))
            p++;
        return p;
    }

    static async Task<int> Run(Args a, CancellationToken cancel)
    {
        var path = ResolvePath(a);
        var names = a.Positional.Skip(1).ToList();
        var runner = new HostRunner(path, s => Console.WriteLine("[host] " + s));
        var quiet = a.Has("quiet");
        runner.Event += e =>
        {
            if (e.Kind == DediEventKind.Line)
            {
                if (!quiet)
                    Console.WriteLine("[" + e.Instance + "] " + e.Text);
                return;
            }
            Console.WriteLine("[" + e.Instance + "] " + e.Kind.ToString().ToUpperInvariant() + " " + e.Text);
        };
        await runner.RunAsync(names, autoUpdate: !a.Has("no-auto-update"), cancel);
        return 0;
    }

    static int Stop(Args a)
    {
        var path = ResolvePath(a);
        var name = a.Positional.Count > 1 ? a.Positional[1] : null;
        var n = DediProcesses.KillUnder(path, name);
        Console.WriteLine("stopped " + n);
        return 0;
    }

    static int Logs(Args a)
    {
        var path = ResolvePath(a);
        if (a.Positional.Count < 2)
            return Fail("usage: r5f-host logs NAME [--lines N]");
        var file = DediSupervisor.LogPath(path, a.Positional[1]);
        if (!File.Exists(file))
            return Fail("no log for " + a.Positional[1]);
        var n = int.TryParse(a.Get("lines"), out var l) ? Math.Clamp(l, 1, 10000) : 100;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var tail = new Queue<string>();
        while (sr.ReadLine() is { } line)
        {
            tail.Enqueue(line);
            if (tail.Count > n)
                tail.Dequeue();
        }
        foreach (var line in tail)
            Console.WriteLine(line);
        return 0;
    }

    static async Task<int> Mods(Args a, CancellationToken cancel)
    {
        var path = ResolvePath(a);
        using var mods = new HostMods(path);
        var status = new Progress<string>(s => Console.WriteLine("  " + s));
        var verb = a.Positional.Count > 1 ? a.Positional[1] : "list";
        var rest = a.Positional.Skip(2).ToList();
        if (verb != "list" && DediProcesses.CountUnder(path) > 0)
            return Fail("stop the server before changing mods");

        switch (verb)
        {
            case "list":
            {
                var locked = mods.ReadLock().Pins;
                var required = ModsStore.ReadRequiredMods(path).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var m in mods.Installed())
                {
                    var pin = locked.TryGetValue(m.ThunderstoreFullName, out var v) ? v : "unlocked";
                    Console.WriteLine($"{m.FolderName,-32} {m.Version,-10} {(m.Enabled ? "on " : "off")} {(required.Contains(m.Id) ? "required" : "optional")} lock {pin}");
                }
                return 0;
            }
            case "add":
                if (rest.Count == 0)
                    return Fail("usage: r5f-host mods add OWNER-NAME[-VERSION]...");
                foreach (var step in await mods.AddAsync(rest, required: !a.Has("optional"), status, cancel))
                    Console.WriteLine("installed " + step.Pin);
                return 0;
            case "remove":
                if (rest.Count != 1)
                    return Fail("usage: r5f-host mods remove OWNER-NAME");
                mods.Remove(rest[0]);
                Console.WriteLine("removed " + rest[0]);
                return 0;
            case "update":
                var moved = await mods.UpdateAllAsync(status, cancel);
                Console.WriteLine(moved.Count == 0 ? "all mods at their newest version" : "updated " + string.Join(", ", moved));
                return 0;
            case "sync":
                await mods.SyncAsync(status, cancel);
                Console.WriteLine("mods match mods.lock.json");
                return 0;
            case "import":
                if (rest.Count != 1)
                    return Fail("usage: r5f-host mods import PROFILE-CODE");
                foreach (var step in await mods.ImportProfileAsync(rest[0], status, cancel))
                    Console.WriteLine("installed " + step.Pin);
                return 0;
        }
        return Fail("usage: r5f-host mods list|add|remove|update|sync|import");
    }
}

/// <summary>--name value / --flag / positionals. Repeated options accumulate.</summary>
sealed class Args
{
    readonly Dictionary<string, List<string>> _opts = new(StringComparer.Ordinal);
    readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    public List<string> Positional { get; } = new();

    static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal)
    {
        "json", "repair", "optional", "no-restart", "no-auto-update", "quiet", "help",
    };

    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            if (t.StartsWith("--", StringComparison.Ordinal) && t.Length > 2)
            {
                var name = t[2..];
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    Add(name[..eq], name[(eq + 1)..]);
                }
                else if (FlagNames.Contains(name) || i + 1 >= argv.Length)
                {
                    _flags.Add(name);
                }
                else
                {
                    Add(name, argv[++i]);
                }
            }
            else
            {
                Positional.Add(t);
            }
        }
    }

    void Add(string name, string value)
    {
        if (!_opts.TryGetValue(name, out var list))
            _opts[name] = list = new List<string>();
        list.Add(value);
    }

    public bool Has(string flag) => _flags.Contains(flag);
    public string? Get(string name) => _opts.TryGetValue(name, out var l) ? l[^1] : null;
    public IEnumerable<string> All(string name) => _opts.TryGetValue(name, out var l) ? l : Enumerable.Empty<string>();
}
