using System.Globalization;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Port of the Windows launcher's MapOption label helpers
/// (R5Flowstate.Spawn/ModeCardViewModel.cs). That project is net8.0-windows, so
/// the two pure string functions are re-homed here; the rules are upstream's
/// exactly, because a map label appears in the server browser, the map picker and
/// the console header, and the stem is what <c>+map</c> actually receives.
///
/// <paramref name="names"/> is the install's stem → friendly-name table. The
/// Linux shell has no playlist catalog yet (it too lives in Spawn), so callers
/// pass null and get the stem alone — which is what the Windows build shows
/// before its catalog loads, not a Linux-only degradation.
/// </summary>
public static class MapLabels
{
    /// <summary>
    /// "mp_rr_desertlands_hu (World's Edge)" when the install names the stem,
    /// otherwise the stem alone. The stem stays visible either way — it is what
    /// gets passed to +map and what a bug report needs to quote.
    /// </summary>
    public static string Label(string stem, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return string.Empty;
        var s = stem.Trim();
        if (names is not null && names.TryGetValue(s, out var friendly) &&
            !string.IsNullOrWhiteSpace(friendly))
        {
            return $"{s}  ({friendly.Trim()})";
        }
        return s;
    }

    /// <summary>Player-facing name: the install's name, else a prettified stem.</summary>
    public static string PlayerName(string stem, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return string.Empty;
        var s = stem.Trim();
        if (names is not null && names.TryGetValue(s, out var friendly) &&
            !string.IsNullOrWhiteSpace(friendly))
            return friendly.Trim();
        if (s.StartsWith("mp_rr_", StringComparison.OrdinalIgnoreCase))
            s = s[6..];
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Replace('_', ' '));
    }
}
