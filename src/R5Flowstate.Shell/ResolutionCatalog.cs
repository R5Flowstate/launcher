using System;
using System.Collections.Generic;
using System.Linq;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>Resolution presets offered by the Local Play "Res" button.</summary>
public static class ResolutionCatalog
{
    public static int DefaultWidth => LaunchArgs.PreferredDefaultMode().Width;
    public static int DefaultHeight => LaunchArgs.PreferredDefaultMode().Height;

    /// <summary>A swap chain smaller or larger than this is refused outright.</summary>
    public const int MinDimension = 320;
    public const int MaxDimension = 16384;

    public readonly record struct Preset(int Width, int Height)
    {
        public string Label => $"{Width} x {Height}";
    }

    public readonly record struct Group(string Caption, IReadOnlyList<Preset> Presets);

    /// <summary>Only modes this GPU lists for exclusive fullscreen, grouped by aspect.</summary>
    public static IReadOnlyList<Group> Groups()
    {
        var groups = LaunchArgs.GroupListedDisplayModes();
        var result = new List<Group>(groups.Count);
        foreach (var g in groups)
        {
            result.Add(new Group(
                g.Caption,
                g.Modes.Select(m => new Preset(m.Width, m.Height)).ToList()));
        }
        return result;
    }

    public static int ClampDimension(int value, int fallback)
        => value >= MinDimension && value <= MaxDimension ? value : fallback;

    public const ClientWindowMode DefaultWindowMode = ClientWindowMode.Fullscreen;

    /// <summary>Window modes offered above the resolution groups, in menu order.</summary>
    public static IReadOnlyList<ClientWindowMode> WindowModes { get; } = new[]
    {
        ClientWindowMode.Windowed,
        ClientWindowMode.Borderless,
        ClientWindowMode.Fullscreen,
    };

    public static ClientWindowMode ClampWindowMode(int value)
        => Enum.IsDefined(typeof(ClientWindowMode), value)
            ? (ClientWindowMode)value
            : DefaultWindowMode;

    /// <summary>Loc key for a mode label: window_mode_windowed / _borderless / _fullscreen.</summary>
    public static string LocKey(ClientWindowMode mode)
        => "window_mode_" + mode.ToString().ToLowerInvariant();
}
