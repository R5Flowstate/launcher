using System.Diagnostics;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Spawns Windows exes via `proton run` with a Steam compat prefix. This is the
/// launcher's only runtime: the game exes are Windows PEs and Proton is what runs
/// them here, and the EA App runs through it too, in the same prefix. Env parity
/// with the Windows spawner: VPROJECT=1, FROM_R5F_LAUNCHER=1.
///
/// <para>The EA App used to have a runtime of its own — the system Wine, in a
/// prefix of its own — on the argument that EA and the game had to be kept apart.
/// The channel between them turned out to be EA's own LSX server on the host's
/// loopback (127.0.0.1:3216), which crosses prefixes anyway, and sharing one
/// prefix is the shape that makes in-game auth work without a bridge. See
/// <see cref="EaChannelProbe"/> for what is measured between them.</para>
///
/// <para>Maintenance steps on a prefix (winetricks, a Wine builtin) go through
/// <see cref="ProtonWine"/> instead: those run Proton's bundled Wine directly
/// rather than a program through Proton.</para>
/// </summary>
public static class ProtonLauncher
{
    public sealed record ProtonRunOptions(
        string ProtonDir,
        string PrefixPath,
        string ExePath,
        string Args = "",
        string WorkingDirectory = "",
        IReadOnlyDictionary<string, string>? ExtraEnv = null,
        bool SetFromLauncher = true,
        bool CaptureInput = false);

    public static string ProtonScriptFor(string protonDir)
        => Path.Combine(protonDir, "proton");

    public static void EnsurePrefixDirs(string prefixPath)
    {
        Directory.CreateDirectory(prefixPath);
    }

    /// <summary>The Steam install Proton is asked to believe in
    /// (<c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>). Shared with
    /// <see cref="DesktopShortcuts"/> so a menu entry and the launcher tell
    /// Proton the same thing.</summary>
    public static string SteamRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamRoot = Path.Combine(home, ".local/share/Steam");
        return Directory.Exists(steamRoot) ? steamRoot : Path.Combine(home, ".steam/steam");
    }

    /// <summary>
    /// The newest log an EA program wrote inside the prefix, or null when there
    /// is none. Two writers matter: the installer (a WiX Burn bootstrapper, which
    /// writes <c>EA_app_&lt;stamp&gt;.log</c> into the prefix user's Temp and is
    /// silent in every other way), and EA Desktop itself.
    ///
    /// This is how "I pressed Install and nothing happened" becomes readable: a
    /// Burn bundle that dies before its window leaves no visible trace, but it
    /// does leave its own account of how far it got.
    /// </summary>
    public static string? FindNewestEaLog(string? prefixPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prefixPath))
                return null;
            var users = Path.Combine(PrefixLayout.DriveCFor(prefixPath), "users");
            if (!Directory.Exists(users))
                return null;

            var candidates = new List<string>();
            foreach (var user in Directory.EnumerateDirectories(users))
            {
                var appData = Path.Combine(user, "AppData");
                candidates.AddRange(EnumerateLogs(Path.Combine(appData, "Local/Temp"), "EA_app_*.log"));
                candidates.AddRange(EnumerateLogs(Path.Combine(appData, "Local/Electronic Arts"), "*.log")
                    .Where(IsEaLogFile));
                candidates.AddRange(EnumerateLogs(Path.Combine(appData, "Roaming/Electronic Arts"), "*.log")
                    .Where(IsEaLogFile));
            }

            return candidates
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The newest Burn bundle log (<c>EA_app_&lt;stamp&gt;.log</c>), which is
    /// the only place the MSI's own log path is named. Kept apart from the
    /// newest-any-log lookup because EA Desktop's log runs while the App does, and a
    /// repair or update would otherwise find EA's log newest and read the bundle's
    /// history out of the wrong file — where <c>WixBundleLog</c> never appears.</summary>
    public static string? FindNewestBundleLog(string? prefixPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prefixPath))
                return null;
            var users = Path.Combine(PrefixLayout.DriveCFor(prefixPath), "users");
            if (!Directory.Exists(users))
                return null;

            var candidates = new List<string>();
            foreach (var user in Directory.EnumerateDirectories(users))
                candidates.AddRange(EnumerateLogs(Path.Combine(user, "AppData", "Local/Temp"), "EA_app_*.log"));

            return candidates
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether a <c>.log</c> under an EA App tree is one of EA's own logs
    /// rather than a Chromium cache file that merely ends in <c>.log</c>.
    ///
    /// EA Desktop is a CEF application, and with it running its LevelDB stores
    /// (<c>…/EA Desktop/CEF/2/EADesktop/BrowserCache/Session Storage/000003.log</c>)
    /// are written continuously — so they are always the newest file matching
    /// <c>*.log</c>. That is how a read-only probe on 2026-09-22 ended up quoting a
    /// session-storage blob as "the newest EA log": a binary file read as text, with
    /// nothing in it about EA's own behaviour.
    /// </summary>
    public static bool IsEaLogFile(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (CacheDirs.Contains(part))
                return false;
        }
        return true;
    }

    static readonly HashSet<string> CacheDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "CEF", "BrowserCache", "Session Storage", "Local Storage", "IndexedDB",
        "Cache", "Code Cache", "GPUCache", "Service Worker", "blob_storage",
        "WebStorage", "Crashpad",
    };

    static IEnumerable<string> EnumerateLogs(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The last <paramref name="count"/> lines of a text file, for a
    /// caller that wants to show what a program said rather than where it said
    /// it.</summary>
    public static IReadOnlyList<string> Tail(string path, int count)
    {
        try
        {
            if (!File.Exists(path) || count <= 0)
                return Array.Empty<string>();
            var all = File.ReadAllLines(path);
            return all.Length <= count ? all : all[(all.Length - count)..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static Process Start(ProtonRunOptions o) => Spawn(o, null);

    /// <summary>Same as <see cref="Start"/>, but the child's stdout/stderr are
    /// piped to <paramref name="onLine"/> instead of being inherited. Wine writes
    /// its launch failures to stderr, so without this a broken launch is
    /// indistinguishable from a working one — the caller gets a pid either way
    /// and has no way to tell that nothing appeared on screen.
    ///
    /// Console lines arrive here too, but the console tab does not use this
    /// entry point: it attaches a <see cref="ConsoleTap"/>, which reads the same
    /// streams itself and needs them raw (ANSI escapes included) rather than one
    /// line at a time through a callback. Both need the child captured, so both
    /// go through this method's rules; only the consumer differs.</summary>
    public static Process StartCaptured(ProtonRunOptions o, Action<string> onLine)
        => Spawn(o, onLine ?? throw new ArgumentNullException(nameof(onLine)));

    /// <summary>Start a child and attach a <see cref="ConsoleTap"/> to it: the
    /// child's stdout/stderr become lines and its stdin becomes commands. This
    /// is what the Console tab launches with — it replaces Windows' named-pipe
    /// tap, which cannot cross the Wine boundary (see <see cref="ConsoleTap"/>).
    ///
    /// The child is started with its streams captured for the same reason
    /// <see cref="StartCaptured"/> exists, so a launch that never appears still
    /// has its stderr to show. Note the two are alternatives over the same
    /// streams: whoever reads first owns them, so a caller picks one.
    ///
    /// The returned tap is not reading yet, and that is deliberate. The child is
    /// already running, so its first lines are the ones that say why a launch
    /// failed — and a tap started before anyone subscribed to it would read
    /// them into nothing. The caller subscribes (its own tail, then the console,
    /// which starts the tap as it takes ownership), and <see cref="ConsoleTap.Start"/>
    /// is idempotent so a caller that starts it itself is harmless.</summary>
    public static (Process Child, ConsoleTap Tap) StartTapped(
        ProtonRunOptions o, LaunchRole role)
    {
        var psi = BuildStartInfo(o with { CaptureInput = true }, capture: true);
        var child = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start proton run.");
        return (child, ConsoleTap.Attach(role, child));
    }

    static Process Spawn(ProtonRunOptions o, Action<string>? onLine)
    {
        var psi = BuildStartInfo(o, capture: onLine is not null);
        if (onLine is null)
            return Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start proton run.");

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        if (!p.Start())
            throw new InvalidOperationException("Failed to start proton run.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>What the log lines call the thing that is about to run, so a log
    /// says which Proton build was used without having to read the env. Deliberately
    /// spawns nothing.</summary>
    public static string Describe(ProtonRunOptions o)
    {
        var dir = o.ProtonDir.TrimEnd('/');
        return $"{Path.GetFileName(dir)} (prefix {o.PrefixPath})";
    }

    /// <summary>The one implementation of the Proton environment. Internal because
    /// it is the seam the suite asserts: a test builds a start info and reads the
    /// environment rather than launching anything.</summary>
    internal static ProcessStartInfo BuildStartInfo(ProtonRunOptions o, bool capture)
    {
        var script = ProtonScriptFor(o.ProtonDir);
        if (!File.Exists(script))
            throw new FileNotFoundException("Proton script not found: " + script);

        EnsurePrefixDirs(o.PrefixPath);
        var steamRoot = SteamRoot();

        var psi = new ProcessStartInfo
        {
            FileName = script,
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            // The console's command line: a dedi launched from the Console tab
            // is handed to a ConsoleTap, which writes commands to its stdin.
            // Opt-in, because a caller that does not want a command channel
            // should leave the child's stdin alone (a redirected stdin that
            // nobody writes to is a stream the child can block on).
            RedirectStandardInput = o.CaptureInput,
            WorkingDirectory = string.IsNullOrWhiteSpace(o.WorkingDirectory)
                ? Path.GetDirectoryName(o.ExePath) ?? o.PrefixPath
                : o.WorkingDirectory,
        };

        // proton run "C:\..." args  — exe here is a Linux path to the Windows binary.
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(o.ExePath);
        if (!string.IsNullOrWhiteSpace(o.Args))
        {
            foreach (var a in SplitArgs(o.Args))
                psi.ArgumentList.Add(a);
        }

        psi.Environment["STEAM_COMPAT_DATA_PATH"] = Path.GetFullPath(o.PrefixPath);
        psi.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot;
        psi.Environment["VPROJECT"] = "1";
        if (o.SetFromLauncher)
            psi.Environment["FROM_R5F_LAUNCHER"] = "1";
        if (o.ExtraEnv is not null)
        {
            foreach (var kv in o.ExtraEnv)
                psi.Environment[kv.Key] = kv.Value;
        }

        return psi;
    }

    /// <summary>True when Wine could not create its GL context — the signature of
    /// a window that will never appear (broken XWayland GLX, no GLX fbconfigs).
    /// Wine prints this to stderr and then runs headless, so the caller sees a
    /// live pid and no window.</summary>
    public static bool LooksLikeGlFailure(string line)
        => line.Contains("X Error of failed request", StringComparison.OrdinalIgnoreCase)
           && line.Contains("GLX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Minimal arg splitter honoring double quotes.</summary>
    internal static IEnumerable<string> SplitArgs(string s)
    {
        var cur = new System.Text.StringBuilder();
        bool inQ = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQ = !inQ; continue; }
            if (char.IsWhiteSpace(ch) && !inQ)
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    // ---- EA App helpers ----

    /// <summary>Find EA Desktop exe inside a Proton prefix. The drive_c comes from
    /// <see cref="PrefixLayout.DriveCFor"/> rather than being spelled out here, so
    /// this and <see cref="FindNewestEaLog"/> cannot disagree about where a prefix
    /// keeps its files — the disagreement that made the log reader blind to a
    /// system-Wine prefix in the first place.</summary>
    public static string? FindEaDesktopExe(string prefixPath)
    {
        if (string.IsNullOrWhiteSpace(prefixPath))
            return null;

        var driveC = PrefixLayout.DriveCFor(prefixPath);
        foreach (var programs in new[] { "Program Files", "Program Files (x86)" })
        {
            var root = Path.Combine(driveC, programs, "Electronic Arts");
            if (!Directory.Exists(root)) continue;
            foreach (var name in new[] { "EALauncher.exe", "EADesktop.exe" })
            {
                try
                {
                    var hit = Directory.GetFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit is not null) return hit;
                }
                catch { }
            }
        }
        return null;
    }

    public static bool IsEaInstalled(string prefixPath) => FindEaDesktopExe(prefixPath) is not null;
}
