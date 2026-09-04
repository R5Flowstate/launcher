using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace R5Flowstate.Spawn;

/// <summary>
/// Launch argv profile. shipping_player is lean; shipping_dev adds -dev/-devsdk and local console knobs.
/// </summary>
public enum LaunchProfile
{
    /// <summary>Player product: no -dev/-devsdk/+sv_cheats.</summary>
    ShippingPlayer,

    /// <summary>Developer: -dev/-devsdk, loopback sockets, verbose script console.</summary>
    ShippingDev,

    /// <summary>Same as ShippingDev today (bat-parity was dropped; playlist no longer forced).</summary>
    OperatorBatParity,
}

/// <summary>How the client presents its swap chain.</summary>
public enum ClientWindowMode
{
    /// <summary>-windowed. A normal titlebar window.</summary>
    Windowed,

    /// <summary>-windowed -noborder. Window with the frame stripped.</summary>
    Borderless,

    /// <summary>Exclusive -fullscreen when the GPU lists this WxH; otherwise -windowed -noborder at that size.</summary>
    Fullscreen,
}

/// <summary>Optional knobs for client argv. Never mixed into dedi builders.</summary>
public sealed class ClientArgOptions
{
    /// <summary>Null leaves the mode to the engine's own video config.</summary>
    public ClientWindowMode? WindowMode { get; init; }
    public bool IncludeConnect { get; init; }
    public string ConnectHost { get; init; } = "127.0.0.1";
    public int ConnectPort { get; init; } = LaunchArgs.DefaultDediPort;

    /// <summary>When true: append -offline and +cl_onlineAuthEnable 0.</summary>
    public bool OfflineNoAuth { get; init; }

    /// <summary>
    /// Public join: never emit -offline, and drop it from extras even if the
    /// Play Local checkbox (or a typed extra) still has it.
    /// </summary>
    public bool ForceOnline { get; init; }

    /// <summary>+bridge_connect_password. Omitted when empty. Same charset as dedi.</summary>
    public string? Password { get; init; }

    /// <summary>+map stem. Omitted when empty. Not mixed with +connect by callers.</summary>
    public string? Map { get; init; }

    /// <summary>Local Play: keep the netchannel alive while the client loads map paks.</summary>
    public bool NoTimeout { get; init; }

    public string? Extra { get; init; }
    public IReadOnlyList<string>? ExtraTokens { get; init; }

    /// <summary>-language &lt;code&gt;. Game locale, same names as the launcher picker.</summary>
    public string? Language { get; init; }

    /// <summary>-width / -height. Both must be positive or neither is emitted.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>Maps to +spire_host_visibility on the dedicated server.</summary>
public enum SpireVisibility
{
    Offline = 0,
    Hidden = 1,
    Public = 2,
}

/// <summary>Optional knobs for dedi argv. Never mixed into client builders.</summary>
public sealed class DediArgOptions
{
    public int Port { get; init; } = LaunchArgs.DefaultDediPort;

    /// <summary>+launchplaylist name (e.g. survival_dev). Game auto-loads playlist defs; no -playlistFile.</summary>
    public string? LaunchPlaylist { get; init; }

    /// <summary>+map stem (e.g. mp_rr_tropic_island_mu2).</summary>
    public string? Map { get; init; }

    /// <summary>When true: append -offline and +sv_onlineAuthEnable 0. Ignored unless Visibility is Offline.</summary>
    public bool OfflineNoAuth { get; init; }

    /// <summary>
    /// +spire_host_visibility. Anything but Offline needs the online auth path,
    /// so it overrides OfflineNoAuth rather than producing a server that
    /// publishes to Spire while refusing to authenticate.
    /// </summary>
    public SpireVisibility Visibility { get; init; } = SpireVisibility.Offline;

    /// <summary>+sv_cheats 1. Independent of the Dev profile.</summary>
    public bool Cheats { get; init; }

    /// <summary>+sv_password. Omitted when empty. Rejected if it carries Cbuf metacharacters.</summary>
    public string? Password { get; init; }

    public string? Extra { get; init; }
    public IReadOnlyList<string>? ExtraTokens { get; init; }
}

/// <summary>
/// Side-split pure argv builders. CLIENT and DEDI lists never merge.
/// Client product base is lean; -dev/-devsdk only on Dev profile.
/// </summary>
public static class LaunchArgs
{
    public const int DefaultDediPort = 37015;

    // Kept for call-site compatibility; playlist is no longer injected by builders.
    public const string DefaultPlaylistFile = "playlists_r5_patch.txt";

    public static IReadOnlyList<string> BuildClientArgs(
        LaunchProfile profile,
        ClientArgOptions? options = null)
    {
        options ??= new ClientArgOptions();
        var tokens = new List<string>();

        Append(tokens, "-nodiscord");
        Append(tokens, "-allowmultiple");

        switch (profile)
        {
            case LaunchProfile.ShippingPlayer:
                break;

            case LaunchProfile.ShippingDev:
            case LaunchProfile.OperatorBatParity:
                Append(tokens, "-devsdk");
                Append(tokens, "-dev");
                AppendDevLocalConsole(tokens);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
        }

        if (options.OfflineNoAuth && !options.ForceOnline)
        {
            if (!ContainsToken(tokens, "-offline"))
                Append(tokens, "-offline");
            AppendPair(tokens, "+cl_onlineAuthEnable", "0");
        }

        // -notimeout also disables the in-game timeout, so a dead host leaves the
        // client parked forever; only the map-load window needs the slack.
        if (options.NoTimeout && !ContainsToken(tokens, "+timeout_during_load"))
            AppendPair(tokens, "+timeout_during_load", "600");

        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            var lang = options.Language.Trim();
            if (!ContainsToken(tokens, "-language"))
            {
                Append(tokens, "-language");
                Append(tokens, lang);
            }
        }

        AppendExtras(tokens, options.Extra, options.ExtraTokens);
        if (options.ForceOnline)
        {
            StripOfflineClientTokens(tokens);
            AppendPair(tokens, "+cl_onlineAuthEnable", "1");
        }

        // After extras so a hand-typed window flag in the box still wins.
        // -forceborder is not emitted for Windowed: this engine treats it as a
        // second spelling of -noborder, so it would strip the frame instead.
        if (options.WindowMode is { } windowMode &&
            !ContainsToken(tokens, "-windowed") &&
            !ContainsToken(tokens, "-fullscreen") &&
            !ContainsToken(tokens, "-noborder"))
        {
            switch (windowMode)
            {
                case ClientWindowMode.Windowed:
                    Append(tokens, "-windowed");
                    break;
                case ClientWindowMode.Borderless:
                    Append(tokens, "-windowed");
                    Append(tokens, "-noborder");
                    break;
                case ClientWindowMode.Fullscreen:
                    // Exclusive only at a GPU-listed mode. A miss used to rewrite
                    // WxH (720p / 1024x768). Unlisted Fullscreen is borderless at
                    // the size the user picked.
                    if (IsListedDisplayMode(options.Width, options.Height))
                        Append(tokens, "-fullscreen");
                    else
                    {
                        Append(tokens, "-windowed");
                        Append(tokens, "-noborder");
                    }
                    break;
            }
        }

        // After extras so a hand-typed -width/-height in the box still wins.
        if (options.Width > 0 && options.Height > 0 &&
            !ContainsToken(tokens, "-width") && !ContainsToken(tokens, "-height"))
        {
            AppendPair(tokens, "-width", options.Width.ToString(CultureInfo.InvariantCulture));
            AppendPair(tokens, "-height", options.Height.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(options.Map))
            AppendPair(tokens, "+map", options.Map.Trim());

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            var pw = options.Password.Trim();
            if (IsSafeServerPassword(pw))
                AppendPair(tokens, "+bridge_connect_password", pw);
        }

        if (options.IncludeConnect)
        {
            var host = string.IsNullOrWhiteSpace(options.ConnectHost)
                ? "127.0.0.1"
                : options.ConnectHost.Trim();
            if (IsSafeConnectHost(host))
            {
                Append(tokens, "+connect");
                Append(tokens, FormatConnectTarget(host, options.ConnectPort));
            }
        }

        return tokens;
    }

    public static IReadOnlyList<string> BuildDediArgs(
        LaunchProfile profile,
        DediArgOptions? options = null)
    {
        options ??= new DediArgOptions();
        var tokens = new List<string>();
        var portStr = options.Port.ToString(CultureInfo.InvariantCulture);

        Append(tokens, "-dedicated");
        AppendPair(tokens, "-port", portStr);

        // Server defaults (always).
        AppendPair(tokens, "+sv_allowSendTableTransmitToClients", "1");
        AppendPair(tokens, "+stringtable_compress", "1");

        switch (profile)
        {
            case LaunchProfile.ShippingPlayer:
                break;

            case LaunchProfile.ShippingDev:
            case LaunchProfile.OperatorBatParity:
                Append(tokens, "-devsdk");
                Append(tokens, "-dev");
                AppendDevLocalConsole(tokens);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
        }

        var hosting = options.Visibility != SpireVisibility.Offline;

        if (options.OfflineNoAuth && !hosting)
        {
            if (!ContainsToken(tokens, "-offline"))
                Append(tokens, "-offline");
            AppendPair(tokens, "+sv_onlineAuthEnable", "0");
        }

        // Always explicit: server.dll defaults this to Public, so an omitted
        // value silently publishes an offline server to the master browser.
        AppendPair(tokens, "+spire_host_visibility",
            ((int)options.Visibility).ToString(CultureInfo.InvariantCulture));

        if (options.Cheats)
            AppendPair(tokens, "+sv_cheats", "1");

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            var pw = options.Password.Trim();
            if (IsSafeServerPassword(pw))
                AppendPair(tokens, "+sv_password", pw);
        }

        if (!string.IsNullOrWhiteSpace(options.LaunchPlaylist))
        {
            var pl = options.LaunchPlaylist.Trim().ToLowerInvariant();
            if (LocalRcon.IsSafeIdentifier(pl))
                AppendPair(tokens, "+launchplaylist", pl);
        }

        if (!string.IsNullOrWhiteSpace(options.Map))
        {
            var mp = options.Map.Trim().ToLowerInvariant();
            if (LocalRcon.IsSafeIdentifier(mp))
                AppendPair(tokens, "+map", mp);
        }

        AppendExtras(tokens, options.Extra, options.ExtraTokens);
        return tokens;
    }

    /// <summary>
    /// In-place hop line. Password omitted when empty. Caller charset-checks both.
    /// </summary>
    public static string FormatBridgeConnectLine(string target, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "bridge_connect " + target;
        if (password.Contains(' ') || password.Contains('\t'))
            return "bridge_connect " + target + " \"" + password + "\"";
        return "bridge_connect " + target + " " + password;
    }

    /// <summary>IPv4 host:port, or [ipv6]:port. Port must be 1..65535.</summary>
    public static string FormatConnectTarget(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";
        else
            host = host.Trim();

        if (port <= 0 || port > 65535)
            port = DefaultDediPort;

        if (host.StartsWith('[') && host.Contains(']'))
            return string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

        if (host.Contains(':'))
            return string.Create(CultureInfo.InvariantCulture, $"[{host}]:{port}");

        return string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
    }

    public static string FormatCommandLine(string exePath, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(exePath));
        if (args is null || args.Count == 0)
            return sb.ToString();

        foreach (var a in args)
        {
            if (string.IsNullOrEmpty(a))
                continue;
            sb.Append(' ');
            sb.Append(NeedsQuote(a) ? Quote(a) : a);
        }

        return sb.ToString();
    }

    public static string FormatArgumentsOnly(IReadOnlyList<string> args)
    {
        if (args is null || args.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (string.IsNullOrEmpty(a))
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(NeedsQuote(a) ? Quote(a) : a);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> SplitExtraTokens(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return Array.Empty<string>();

        var result = new List<string>();
        var p = 0;
        var s = extra;

        while (p < s.Length)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p]))
                p++;
            if (p >= s.Length)
                break;

            if (s[p] == '"')
            {
                p++;
                var start = p;
                while (p < s.Length && s[p] != '"')
                    p++;
                if (p > start)
                    result.Add(s.Substring(start, p - start));
                if (p < s.Length && s[p] == '"')
                    p++;
                continue;
            }

            var tokStart = p;
            while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != '"')
                p++;
            if (p > tokStart)
                result.Add(s.Substring(tokStart, p - tokStart));
        }

        return result;
    }

    public static IReadOnlyList<string> BuildClientShippingPlayer(
        ClientWindowMode? windowMode = null,
        string? connectHostPort = null,
        string playlistFile = DefaultPlaylistFile,
        IEnumerable<string>? extra = null)
    {
        _ = playlistFile;
        string? host = null;
        var port = DefaultDediPort;
        var include = false;
        if (!string.IsNullOrWhiteSpace(connectHostPort))
        {
            include = true;
            var parts = connectHostPort.Split(':', 2);
            host = parts[0];
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
        }

        return BuildClientArgs(LaunchProfile.ShippingPlayer, new ClientArgOptions
        {
            WindowMode = windowMode,
            IncludeConnect = include,
            ConnectHost = host ?? "127.0.0.1",
            ConnectPort = port,
            ExtraTokens = extra?.ToList(),
        });
    }

    public static IReadOnlyList<string> BuildDediShippingPlayer(
        int port = DefaultDediPort,
        string playlistFile = DefaultPlaylistFile,
        IEnumerable<string>? extra = null)
    {
        _ = playlistFile;
        return BuildDediArgs(LaunchProfile.ShippingPlayer, new DediArgOptions
        {
            Port = port,
            ExtraTokens = extra?.ToList(),
        });
    }

    public static IReadOnlyList<string> BuildClientShippingDev(
        string playlistFile = DefaultPlaylistFile,
        ClientWindowMode? windowMode = null,
        string? connectHostPort = null)
    {
        _ = playlistFile;
        string? host = null;
        var port = DefaultDediPort;
        var include = false;
        if (!string.IsNullOrWhiteSpace(connectHostPort))
        {
            include = true;
            var parts = connectHostPort.Split(':', 2);
            host = parts[0];
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
        }

        return BuildClientArgs(LaunchProfile.ShippingDev, new ClientArgOptions
        {
            WindowMode = windowMode,
            IncludeConnect = include,
            ConnectHost = host ?? "127.0.0.1",
            ConnectPort = port,
        });
    }

    public static IReadOnlyList<string> BuildDediShippingDev(
        int port = DefaultDediPort,
        string playlistFile = DefaultPlaylistFile)
    {
        _ = playlistFile;
        return BuildDediArgs(LaunchProfile.ShippingDev, new DediArgOptions
        {
            Port = port,
        });
    }

    private static void AppendDevLocalConsole(List<string> tokens)
    {
        AppendPair(tokens, "+net_usesocketsforloopback", "1");
        AppendPair(tokens, "+script_show_output", "2");
        AppendPair(tokens, "+script_show_warning", "2");
    }

    private static void AppendExtras(List<string> tokens, string? extra, IReadOnlyList<string>? extraTokens)
    {
        if (extraTokens is not null)
        {
            foreach (var t in extraTokens)
                Append(tokens, t);
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var t in SplitExtraTokens(extra))
                Append(tokens, t);
        }
    }

    /// <summary>Exact 16:9. 1366x768 is not; 1280x720 and 1920x1080 are.</summary>
    public static bool IsExactSixteenNine(int width, int height)
        => width > 0 && height > 0 && (long)width * 9 == (long)height * 16;

    public readonly record struct DisplayMode(int Width, int Height);

    /// <summary>Test seam. Null queries the primary adapter via EnumDisplaySettings.</summary>
    public static Func<IReadOnlyList<DisplayMode>>? DisplayModesOverride { get; set; }

    private static IReadOnlyList<DisplayMode>? s_cachedWin32Modes;

    public static IReadOnlyList<DisplayMode> GetDisplayModes()
        => DisplayModesOverride?.Invoke() ?? (s_cachedWin32Modes ??= QueryWin32DisplayModes());

    public static bool IsListedDisplayMode(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        var modes = GetDisplayModes();
        for (var i = 0; i < modes.Count; i++)
        {
            if (modes[i].Width == width && modes[i].Height == height)
                return true;
        }
        return false;
    }

    /// <summary>w1/h1 == w2/h2 with no float slop. 1280x1024 is 5:4, not 4:3.</summary>
    public static bool SameIntegerAspect(int w1, int h1, int w2, int h2)
        => w1 > 0 && h1 > 0 && w2 > 0 && h2 > 0 && (long)w1 * h2 == (long)w2 * h1;

    public readonly record struct ModeGroup(string Caption, IReadOnlyList<DisplayMode> Modes);

    public const int MinListedWidth = 640;
    public const int MinListedHeight = 480;

    /// <summary>GPU-listed modes grouped by integer aspect. Empty groups omitted.</summary>
    public static IReadOnlyList<ModeGroup> GroupListedDisplayModes()
    {
        var order = new[] { "4:3", "5:4", "16:10", "16:9", "Other" };
        var buckets = new Dictionary<string, List<DisplayMode>>(StringComparer.Ordinal);
        foreach (var name in order)
            buckets[name] = new List<DisplayMode>();

        foreach (var m in GetDisplayModes())
        {
            if (m.Width < MinListedWidth || m.Height < MinListedHeight)
                continue;
            buckets[AspectCaption(m.Width, m.Height)].Add(m);
        }

        foreach (var list in buckets.Values)
        {
            list.Sort((a, b) =>
            {
                var c = a.Width.CompareTo(b.Width);
                return c != 0 ? c : a.Height.CompareTo(b.Height);
            });
        }

        var result = new List<ModeGroup>();
        foreach (var name in order)
        {
            if (buckets[name].Count == 0)
                continue;
            result.Add(new ModeGroup(name, buckets[name]));
        }
        return result;
    }

    public static DisplayMode PreferredDefaultMode()
    {
        var desktop = new DisplayMode(GetSystemMetrics(0), GetSystemMetrics(1));
        if (IsListedDisplayMode(desktop.Width, desktop.Height))
            return desktop;

        var best = new DisplayMode(1920, 1080);
        var bestPixels = -1;
        foreach (var m in GetDisplayModes())
        {
            if (m.Width < MinListedWidth || m.Height < MinListedHeight)
                continue;
            var pixels = m.Width * m.Height;
            if (pixels > bestPixels)
            {
                bestPixels = pixels;
                best = m;
            }
        }
        return best;
    }

    public static string AspectCaption(int width, int height)
    {
        if (SameIntegerAspect(width, height, 4, 3))
            return "4:3";
        if (SameIntegerAspect(width, height, 5, 4))
            return "5:4";
        if (SameIntegerAspect(width, height, 16, 10))
            return "16:10";
        if (SameIntegerAspect(width, height, 16, 9))
            return "16:9";
        return "Other";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    private static IReadOnlyList<DisplayMode> QueryWin32DisplayModes()
    {
        var list = new List<DisplayMode>();
        var seen = new HashSet<long>();
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            var key = ((long)dm.dmPelsWidth << 32) | (uint)dm.dmPelsHeight;
            if (!seen.Add(key))
                continue;
            list.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight));
        }

        return list;
    }

    /// <summary>Drop -offline / -noorigin / +cl_onlineAuthEnable 0 from a client argv.</summary>
    public static void StripOfflineClientTokens(List<string> tokens)
    {
        for (var i = 0; i < tokens.Count; )
        {
            var t = tokens[i];
            if (string.Equals(t, "-offline", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, "-noorigin", StringComparison.OrdinalIgnoreCase))
            {
                tokens.RemoveAt(i);
                continue;
            }

            if (string.Equals(t, "+cl_onlineAuthEnable", StringComparison.OrdinalIgnoreCase)
                && i + 1 < tokens.Count
                && tokens[i + 1] == "0")
            {
                tokens.RemoveAt(i);
                tokens.RemoveAt(i);
                continue;
            }

            i++;
        }
    }

    private static void Append(List<string> tokens, string? token)
    {
        if (!string.IsNullOrEmpty(token))
            tokens.Add(token);
    }

    private static void AppendPair(List<string> tokens, string key, string? value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            return;
        tokens.Add(key);
        tokens.Add(value);
    }

    /// <summary>
    /// Same charset the dedi join path accepts: printable, no quote/semicolon/backslash, max 128.
    /// </summary>
    public static bool IsSafeServerPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return true;
        if (password.Length > 128)
            return false;
        foreach (var c in password)
        {
            if (c < 32 || c == 127 || c == '"' || c == ';' || c == '\\')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Host portion for +connect / bridge_connect: ascii alnum plus . : - _ ,
    /// length 1..64, must contain an alnum. Port is validated separately.
    /// </summary>
    public static bool IsSafeConnectHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var h = host.Trim();
        if (h.Length is 0 or > 64)
            return false;

        if (h.StartsWith('[') && h.EndsWith(']'))
            h = h[1..^1];

        var sawAlnum = false;
        foreach (var c in h)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '_';
            if (!ok)
                return false;
            if (char.IsAsciiLetterOrDigit(c))
                sawAlnum = true;
        }

        return sawAlnum;
    }

    /// <summary>Replace +sv_password values so the preview never shows the secret.</summary>
    public static string RedactSensitiveArgs(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var tokens = SplitExtraTokens(line);
        if (tokens.Count == 0)
            return line;

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            var t = tokens[i];
            sb.Append(NeedsQuote(t) ? Quote(t) : t);
            if ((string.Equals(t, "+sv_password", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "+bridge_connect_password", StringComparison.OrdinalIgnoreCase))
                && i + 1 < tokens.Count)
            {
                sb.Append(" ****");
                i++;
            }
        }
        return sb.ToString();
    }

    private static bool ContainsToken(List<string> tokens, string token)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool NeedsQuote(string s) =>
        s.Length == 0 || s.Contains(' ') || s.Contains('\t') || s.Contains('"') || s.StartsWith('[');

    private static string Quote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
