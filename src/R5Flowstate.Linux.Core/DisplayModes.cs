using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// What the Res button offers: this machine's display modes, grouped by aspect,
/// plus the three window modes. Ported from the Windows shell's
/// <c>ResolutionCatalog</c>, which asks Win32 (<c>EnumDisplaySettings</c>) and
/// <c>SM_CXSCREEN</c>/<c>SM_CYSCREEN</c>.
///
/// The grouping, the ordering, the minimum listed size, the clamp range and the
/// default are all Windows' — the menu has to read the same on both platforms, and
/// its captions are compared by eye against upstream's. What differs is only where
/// the numbers come from:
///
///   kscreen-doctor -o  →  xrandr -q  →  a built-in preset list
///
/// in that order, because on a Wayland session <c>xrandr</c> talks to whatever
/// Xwayland happens to be running and answers with Xwayland's own modelist rather
/// than the panel's (measured on the development box: xrandr listed 320x240 and
/// 320x200 for a 1920x1080 DisplayPort panel, which kscreen-doctor does not).
/// xrandr is still the second choice because an X11 session or a bare X server
/// with no KDE around it has kscreen-doctor and nothing else.
///
/// Nothing here is a hard dependency: a machine with neither tool gets the preset
/// list, which is exactly what upstream falls back to (1920x1080) and is a Res menu
/// that works rather than one that is empty. Every parse is total — an unreadable
/// answer is an empty list, never an exception, because this runs on the UI thread
/// while a menu is opening.
/// </summary>
public static class DisplayModes
{
    /// <summary>A swap chain smaller or larger than this is refused outright.</summary>
    public const int MinDimension = 320;
    public const int MaxDimension = 16384;

    /// <summary>Below this a mode is a video mode, not a game resolution.</summary>
    public const int MinListedWidth = 640;
    public const int MinListedHeight = 480;

    public readonly record struct DisplayMode(int Width, int Height);

    public readonly record struct ModeGroup(string Caption, IReadOnlyList<DisplayMode> Modes);

    /// <summary>The Res menu's top section, in order. Strings rather than an enum
    /// because this is what <see cref="LaunchArgBuilder.BuildClient"/> takes and what
    /// <c>LinuxSettings.ClientWindowMode</c> stores, so there is one spelling from the
    /// settings file to the game's command line.</summary>
    public static IReadOnlyList<string> WindowModes { get; } = new[]
    {
        Windowed, Borderless, Fullscreen,
    };

    public const string Windowed = "windowed";
    public const string Borderless = "borderless";
    public const string Fullscreen = "fullscreen";

    /// <summary>Fullscreen, like Windows (<c>ResolutionCatalog.DefaultWindowMode</c>).
    /// A settings file with no mode in it gets the same flag a fresh Windows install
    /// would send, not "whatever the game does on its own".</summary>
    public const string DefaultWindowMode = Fullscreen;

    /// <summary>Test seam, upstream's shape: null consults the session.</summary>
    public static Func<IReadOnlyList<DisplayMode>>? ModesOverride { get; set; }

    /// <summary>Test seam: null consults the session's current mode.</summary>
    public static Func<DisplayMode>? DesktopOverride { get; set; }

    static IReadOnlyList<DisplayMode>? s_cached;
    static DisplayMode? s_desktop;


    /// <summary>
    /// Which of the three answered the last read — "kscreen-doctor", "xrandr" or
    /// "presets" — and the raw text it answered with. Diagnostics for
    /// <c>--display-probe</c> and nothing else; the launcher never branches on
    /// either, because a Res menu does not care where its numbers came from.
    /// </summary>
    public static string Source { get; private set; } = "not read yet";

    /// <summary>The raw output of an enumeration tool, exactly as it arrived, for
    /// the probe's transcript. Null when the tool is not there.</summary>
    public static string? Raw(string tool) => tool switch
    {
        "kscreen-doctor" => Run("kscreen-doctor", "-o"),
        "xrandr" => Run("xrandr", "-q"),
        _ => null,
    };

    /// <summary>
    /// This machine's modes, newest read first, deduplicated and sorted. Cached for
    /// the process: the answer cannot change without the session being reconfigured
    /// out from under the launcher, and re-shelling out on every menu open would put
    /// a process spawn in front of a click.
    /// </summary>
    public static IReadOnlyList<DisplayMode> Get()
        => ModesOverride?.Invoke() ?? (s_cached ??= Read());

    /// <summary>Forget the cached read. The suite uses it between fixtures.</summary>
    public static void Invalidate()
    {
        s_cached = null;
        s_desktop = null;
        Source = "not read yet";
    }

    /// <summary>
    /// One spawn per tool, and both answers out of that one spawn: the modes and the
    /// geometry arrive in the same output, so a second call to ask for the desktop
    /// size would be a second process for a number already in hand.
    /// </summary>
    static IReadOnlyList<DisplayMode> Read()
    {
        var ksText = Run("kscreen-doctor", "-o");
        var ks = ParseKScreenDoctor(ksText);
        if (ks.Count > 0)
        {
            Source = "kscreen-doctor";
            s_desktop = ParseKScreenDoctorGeometry(ksText);
            return ks;
        }

        var xrText = Run("xrandr", "-q");
        var xr = ParseXrandr(xrText);
        if (xr.Count > 0)
        {
            Source = "xrandr";
            s_desktop = ParseXrandrCurrent(xrText);
            return xr;
        }

        Source = "presets";
        return Presets();
    }

    /// <summary>
    /// The size the session is running at now. Reading it also fills in the modes,
    /// because they come from the same output — and when a test seam has taken over
    /// the modes, nothing is read from the session at all, which is what keeps a
    /// suite run off the real display.
    /// </summary>
    public static DisplayMode Desktop()
    {
        if (DesktopOverride is not null)
            return DesktopOverride();
        if (s_desktop is null)
            Get();
        return s_desktop ?? default;
    }

    /// <summary>Was this size handed to us by the session (as opposed to typed in)?</summary>
    public static bool IsListedDisplayMode(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        foreach (var m in Get())
        {
            if (m.Width == width && m.Height == height)
                return true;
        }
        return false;
    }

    /// <summary>w1/h1 == w2/h2 with no float slop. 1280x1024 is 5:4, not 4:3.</summary>
    public static bool SameIntegerAspect(int w1, int h1, int w2, int h2)
        => w1 > 0 && h1 > 0 && w2 > 0 && h2 > 0 && (long)w1 * h2 == (long)w2 * h1;

    public static string AspectCaption(int width, int height)
    {
        if (SameIntegerAspect(width, height, 4, 3)) return "4:3";
        if (SameIntegerAspect(width, height, 5, 4)) return "5:4";
        if (SameIntegerAspect(width, height, 16, 10)) return "16:10";
        if (SameIntegerAspect(width, height, 16, 9)) return "16:9";
        return "Other";
    }

    /// <summary>
    /// Listed modes grouped by integer aspect, the groups in Windows' fixed order
    /// and empty groups dropped — the same five captions upstream's menu shows.
    /// </summary>
    public static IReadOnlyList<ModeGroup> Groups()
    {
        var order = new[] { "4:3", "5:4", "16:10", "16:9", "Other" };
        var buckets = new Dictionary<string, List<DisplayMode>>(StringComparer.Ordinal);
        foreach (var name in order)
            buckets[name] = new List<DisplayMode>();

        foreach (var m in Get())
        {
            if (m.Width < MinListedWidth || m.Height < MinListedHeight)
                continue;
            buckets[AspectCaption(m.Width, m.Height)].Add(m);
        }

        var result = new List<ModeGroup>();
        foreach (var name in order)
        {
            var list = buckets[name];
            if (list.Count == 0)
                continue;
            list.Sort((a, b) =>
            {
                var c = a.Width.CompareTo(b.Width);
                return c != 0 ? c : a.Height.CompareTo(b.Height);
            });
            result.Add(new ModeGroup(name, list));
        }
        return result;
    }

    /// <summary>
    /// What a fresh install should launch at: the session's own size when it is a
    /// mode the session lists, else the biggest listed mode, else 1920x1080 — which
    /// is upstream's hardcoded fallback and is only reached when nothing could be
    /// enumerated at all.
    /// </summary>
    public static DisplayMode PreferredDefaultMode()
    {
        var desktop = Desktop();
        if (IsListedDisplayMode(desktop.Width, desktop.Height))
            return desktop;

        var best = new DisplayMode(1920, 1080);
        var bestPixels = -1;
        foreach (var m in Get())
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

    /// <summary>A typed-in dimension, or the fallback when it is outside the range.</summary>
    public static int ClampDimension(int value, int fallback)
        => value >= MinDimension && value <= MaxDimension ? value : fallback;

    /// <summary>
    /// A window mode from anywhere (settings file, command line, hand edit). Case
    /// and surrounding space are forgiven; anything unrecognised — including the
    /// empty string an older settings file has — becomes fullscreen, because the
    /// alternative is a menu with nothing ticked and no flag on the launch line.
    /// </summary>
    public static string ClampWindowMode(string? value)
    {
        var v = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return v switch
        {
            Windowed => Windowed,
            Borderless => Borderless,
            Fullscreen => Fullscreen,
            _ => DefaultWindowMode,
        };
    }

    /// <summary>Loc key for a mode label: window_mode_windowed / _borderless / _fullscreen.</summary>
    public static string LocKey(string mode) => "window_mode_" + ClampWindowMode(mode);

    /// <summary>For the log line after a change, e.g. "Borderless".</summary>
    public static string ModeLabel(string mode) => ClampWindowMode(mode) switch
    {
        Windowed => "windowed",
        Borderless => "windowed, borderless",
        _ => "fullscreen",
    };

    /// <summary>When nothing could be enumerated. Deliberately short and ordinary:
    /// five sizes that a monitor or a TV will accept, no 4K, and a size outside the
    /// list is still typed into the boxes by hand.</summary>
    public static IReadOnlyList<DisplayMode> Presets() => new[]
    {
        new DisplayMode(1280, 720),
        new DisplayMode(1600, 900),
        new DisplayMode(1920, 1080),
        new DisplayMode(2560, 1440),
        new DisplayMode(3840, 2160),
    };

    // ------------------------------------------------------------ enumeration

    /// <summary>kscreen-doctor colourises even when its output is not a terminal.</summary>
    static readonly Regex s_ansi = new("\u001b\\[[0-9;]*m", RegexOptions.Compiled);

    static readonly Regex s_mode = new(@"(\d{3,5})x(\d{3,5})@", RegexOptions.Compiled);

    static readonly Regex s_geometry = new(@"Geometry:\s*\S+\s+(\d{3,5})x(\d{3,5})", RegexOptions.Compiled);

    static readonly Regex s_xrandrCurrent = new(@"current\s+(\d{3,5})\s*x\s*(\d{3,5})", RegexOptions.Compiled);

    static readonly Regex s_xrandrMode = new(@"^\s+(\d{3,5})x(\d{3,5})\s+[\d.]+\s*[*+]?", RegexOptions.Compiled);

    static string? Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null)
                return null;
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return p.ExitCode == 0 ? text : null;
        }
        catch
        {
            // Not installed, not on PATH, no permission — all the same answer here.
            return null;
        }
    }

    /// <summary>
    /// kscreen-doctor -o. One <c>Modes:</c> line per output carrying every mode as
    /// <c>N:1920x1080@180.00*</c>; the <c>*</c> marks the current one and <c>!</c>
    /// the preferred, and both are ignored because this is the list of what can be
    /// asked for, not of what is up.
    /// </summary>
    internal static IReadOnlyList<DisplayMode> ParseKScreenDoctor(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<DisplayMode>();
        var text = s_ansi.Replace(output, string.Empty);
        var seen = new HashSet<long>();
        var list = new List<DisplayMode>();
        foreach (var line in text.Split('\n'))
        {
            // Only the mode lines: a Geometry or a Scale line holds a size too, and
            // neither of them is a mode that can be picked.
            if (!line.Contains('@', StringComparison.Ordinal))
                continue;
            foreach (Match m in s_mode.Matches(line))
            {
                var mode = Mode(m.Groups[1].Value, m.Groups[2].Value);
                if (mode.Width > 0 && seen.Add(Key(mode)))
                    list.Add(mode);
            }
        }
        list.Sort(Compare);
        return list;
    }

    /// <summary>The Geometry line: "Geometry: 0,0 1920x1080", i.e. where the output
    /// is placed and the size it is at.</summary>
    internal static DisplayMode? ParseKScreenDoctorGeometry(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var m = s_geometry.Match(s_ansi.Replace(output, string.Empty));
        return m.Success ? Mode(m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// <summary>
    /// xrandr -q. The Screen line's "current W x H" is the session size; each
    /// output's indented mode lines are its modes. The same regexes are kept apart
    /// from kscreen-doctor's because xrandr writes the size and the refresh rate as
    /// two columns, not as <c>WxH@rate</c>.
    /// </summary>
    internal static IReadOnlyList<DisplayMode> ParseXrandr(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<DisplayMode>();
        var seen = new HashSet<long>();
        var list = new List<DisplayMode>();
        foreach (var line in output.Split('\n'))
        {
            var m = s_xrandrMode.Match(line);
            if (!m.Success)
                continue;
            var mode = Mode(m.Groups[1].Value, m.Groups[2].Value);
            if (mode.Width > 0 && seen.Add(Key(mode)))
                list.Add(mode);
        }
        list.Sort(Compare);
        return list;
    }

    internal static DisplayMode? ParseXrandrCurrent(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var m = s_xrandrCurrent.Match(output);
        return m.Success ? Mode(m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    static DisplayMode Mode(string w, string h)
        => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
           && int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? new DisplayMode(width, height)
            : default;

    static long Key(DisplayMode m) => ((long)m.Width << 32) | (uint)m.Height;

    static int Compare(DisplayMode a, DisplayMode b)
    {
        var c = a.Width.CompareTo(b.Width);
        return c != 0 ? c : a.Height.CompareTo(b.Height);
    }

    /// <summary>"1920 x 1080", the label the menu shows (Windows' Preset.Label).</summary>
    public static string Label(DisplayMode mode) => $"{mode.Width} x {mode.Height}";

    /// <summary>One line for the log, after a menu pick or a typed size.</summary>
    public static string Describe(int width, int height)
        => width <= 0 || height <= 0
            ? "game default (no override)"
            : $"{width}x{height}";
}
