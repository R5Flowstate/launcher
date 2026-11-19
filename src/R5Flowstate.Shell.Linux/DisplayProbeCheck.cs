using System;
using System.Collections.Generic;
using System.Linq;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the display lane: what the Res button would offer on this
/// machine, which tool answered, and what the three window modes put on the launch
/// line. This is how the resolution half of the Play tab is checked here without
/// launching a window.
///
///     R5Flowstate --display-probe            the menu's contents and the three launch lines
///     R5Flowstate --display-probe --raw      also print both tools' output as it arrived
///
/// <para><b>What "read-only" means here, precisely.</b> It reads nothing of the
/// player's and writes nothing at all: no settings key, no cache, no prefix. It asks
/// the session two questions (<c>kscreen-doctor -o</c>, <c>xrandr -q</c>), and it
/// builds the launch line in memory without starting anything.</para>
///
/// <para>Exit codes: <c>0</c> a display answered — the menu is built from this
/// machine's own modes; <c>2</c> neither tool could be run, so the menu would fall
/// back to the five built-in presets; <c>3</c> a tool answered but held no mode the
/// menu would list — a session too small to offer a game resolution.</para>
/// </summary>
static class DisplayProbeCheck
{
    public static int Run(bool raw)
    {
        Loc.Initialize(null);

        Console.WriteLine("Display probe (read-only: nothing is written, nothing is started, "
                          + "and the launch lines are built in memory).");
        Console.WriteLine();

        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)";
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        Console.WriteLine($"  session      {session}");
        Console.WriteLine($"  wayland      {(string.IsNullOrWhiteSpace(wayland) ? "(none)" : wayland)}");
        Console.WriteLine($"  display      {(string.IsNullOrWhiteSpace(display) ? "(none)" : display)}");
        Console.WriteLine("               (this decides which tool can answer at all: xrandr talks to");
        Console.WriteLine("                Xwayland on a Wayland session, and answers with Xwayland's");
        Console.WriteLine("                own mode list rather than the panel's)");
        Console.WriteLine();

        if (raw)
        {
            foreach (var tool in new[] { "kscreen-doctor", "xrandr" })
            {
                var text = DisplayModes.Raw(tool);
                Console.WriteLine($"  {tool} -o/-q  {(text is null ? "not installed, or it refused to run" : "")}");
                foreach (var line in (text ?? string.Empty).Split('\n'))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (trimmed.Length > 0)
                        Console.WriteLine($"    | {trimmed.Replace("\u001b", "\\e")}");
                }
                Console.WriteLine();
            }
        }

        var modes = DisplayModes.Get();
        Console.WriteLine($"  read by      {DisplayModes.Source}");
        Console.WriteLine($"  desktop      {Describe(DisplayModes.Desktop())}");
        Console.WriteLine($"  modes        {modes.Count}");
        Console.WriteLine();

        if (modes.Count > 0)
        {
            Console.WriteLine("  every mode the session lists (the menu filters these):");
            foreach (var m in modes)
                Console.WriteLine($"    {Describe(m),-14} {DisplayModes.AspectCaption(m.Width, m.Height)}");
            Console.WriteLine();
        }

        Console.WriteLine("  the menu's groups (what the Res button shows):");
        var groups = DisplayModes.Groups();
        foreach (var group in groups)
        {
            Console.WriteLine($"    {group.Caption}");
            foreach (var preset in group.Modes)
                Console.WriteLine($"      {DisplayModes.Label(preset)}");
        }
        if (groups.Count == 0)
            Console.WriteLine("    (none — every mode this session lists is smaller than "
                              + $"{DisplayModes.MinListedWidth}x{DisplayModes.MinListedHeight})");
        Console.WriteLine();

        var preferred = DisplayModes.PreferredDefaultMode();
        Console.WriteLine($"  default mode {Describe(preferred)}"
                          + (DisplayModes.IsListedDisplayMode(preferred.Width, preferred.Height)
                              ? "  (this session lists it)"
                              : "  (nothing matched, so this is the built-in 1920x1080)"));
        Console.WriteLine();

        // The clamp, demonstrated rather than described: these five answers are the
        // whole rule, and they are the same on every machine.
        Console.WriteLine("  clamp        (320–16384 in, else the fallback out)");
        foreach (var value in new[] { 0, 320, 1920, 16384, 16385, -100 })
            Console.WriteLine($"    {value,7} -> {DisplayModes.ClampDimension(value, 0)}");
        Console.WriteLine();

        Console.WriteLine("  window modes (what the menu's first section offers):");
        foreach (var mode in DisplayModes.WindowModes)
        {
            var line = LaunchArgBuilder.BuildClient(
                dev: false, offlineNoAuth: false, forceOnline: false,
                language: "english", extra: null,
                windowMode: mode, width: preferred.Width, height: preferred.Height,
                map: null, password: null, connect: null);
            Console.WriteLine($"    {DisplayModes.LocKey(mode),-24} {Loc.Get(DisplayModes.LocKey(mode))}");
            Console.WriteLine($"      flags  {Flags(line)}");
        }
        Console.WriteLine();

        // The other half of the rule: 0x0 is the menu's "Game default" item, and it
        // puts no size on the line at all.
        var noSize = LaunchArgBuilder.BuildClient(
            dev: false, offlineNoAuth: false, forceOnline: false,
            language: "english", extra: null,
            windowMode: DisplayModes.DefaultWindowMode, width: 0, height: 0,
            map: null, password: null, connect: null);
        Console.WriteLine($"  game default (0 x 0): {Flags(noSize)}");
        Console.WriteLine();

        if (modes.Count == 0)
        {
            Console.WriteLine("EXIT 2  neither kscreen-doctor nor xrandr answered: the Res menu "
                              + "would offer the five built-in presets instead of this machine's modes.");
            return 2;
        }

        if (groups.Count == 0)
        {
            Console.WriteLine("EXIT 3  a display answered but lists no mode the menu would show.");
            return 3;
        }

        Console.WriteLine($"EXIT 0  {groups.Count} aspect group(s) and "
                          + $"{groups.Sum(g => g.Modes.Count)} resolution(s) come from "
                          + $"{DisplayModes.Source}; the three window modes each put their own "
                          + "flag on the line.");
        return 0;
    }

    static string Describe(DisplayModes.DisplayMode mode)
        => mode.Width > 0 && mode.Height > 0 ? $"{mode.Width}x{mode.Height}" : "(unknown)";

    /// <summary>Just the resolution and window flags, so the transcript shows the
    /// part of the line this probe is about rather than the whole client command.</summary>
    static string Flags(IReadOnlyList<string> line)
    {
        var wanted = line.Where(a => a is "-windowed" or "-noborder" or "-fullscreen"
                                     or "-width" or "-height").ToList();
        var outList = new List<string>();
        for (var i = 0; i < line.Count; i++)
        {
            if (!wanted.Contains(line[i]))
                continue;
            outList.Add(line[i]);
            if ((line[i] == "-width" || line[i] == "-height") && i + 1 < line.Count)
                outList.Add(line[++i]);
        }
        return outList.Count == 0 ? "(none — the game chooses)" : string.Join(' ', outList);
    }
}
