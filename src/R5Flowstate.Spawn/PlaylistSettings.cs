using System.Globalization;
using System.Text.RegularExpressions;

namespace R5Flowstate.Spawn;

public enum PlaylistSettingKind
{
    Int,
    Float,
    Bool,
    Choice,
    Weapon,
}

/// <summary>
/// One host setting a playlist opens to runtime overrides:
/// <c>vars { r5f_setting_&lt;var&gt; "&lt;type&gt; [args]|&lt;label&gt;" }</c>.
/// The SDK applies the same rules on the dedi and on every client, so a value
/// that passes here is one both engines accept.
/// </summary>
public sealed class PlaylistSetting
{
    public const string DeclPrefix = "r5f_setting_";

    public required string Var { get; init; }
    public required string Label { get; init; }
    public PlaylistSettingKind Kind { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>The playlist's own value for the var; empty when it never sets it.</summary>
    public string Default { get; init; } = string.Empty;

    public static PlaylistSetting? Parse(string var, string decl, string? defaultValue)
    {
        if (string.IsNullOrWhiteSpace(var) || string.IsNullOrWhiteSpace(decl) ||
            var.StartsWith(DeclPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var bar = decl.IndexOf('|');
        var spec = bar >= 0 ? decl[..bar] : decl;
        var label = bar >= 0 && bar + 1 < decl.Length ? decl[(bar + 1)..].Trim() : var;
        var tok = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0)
            return null;

        var def = defaultValue?.Trim() ?? string.Empty;
        switch (tok[0].ToLowerInvariant())
        {
            case "bool":
                return new PlaylistSetting { Var = var, Label = label, Kind = PlaylistSettingKind.Bool, Default = def };
            case "weapon":
                return new PlaylistSetting { Var = var, Label = label, Kind = PlaylistSettingKind.Weapon, Default = def };
            case "choice":
                if (tok.Length < 2)
                    return null;
                return new PlaylistSetting
                {
                    Var = var, Label = label, Kind = PlaylistSettingKind.Choice,
                    Choices = tok.Skip(1).ToList(), Default = def,
                };
            case "int":
            case "float":
            {
                var integer = tok[0].Equals("int", StringComparison.OrdinalIgnoreCase);
                if (tok.Length < 3 || !TryNumber(tok[1], integer, out var min) || !TryNumber(tok[2], integer, out var max))
                    return null;
                return new PlaylistSetting
                {
                    Var = var, Label = label,
                    Kind = integer ? PlaylistSettingKind.Int : PlaylistSettingKind.Float,
                    Min = min, Max = max, Default = def,
                };
            }
            default:
                return null;
        }
    }

    /// <summary>Same rules as the SDK's Playlists_ValidateSetting.</summary>
    public bool Validate(string? value, out string reason)
    {
        reason = string.Empty;
        var v = value?.Trim() ?? string.Empty;
        if (v.Length == 0)
        {
            reason = "empty value";
            return false;
        }
        if (v.Length > 63)
        {
            reason = "value is longer than 63 characters";
            return false;
        }
        foreach (var c in v)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '+'))
            {
                reason = "only letters, digits and _ . + - are allowed";
                return false;
            }
        }

        switch (Kind)
        {
            case PlaylistSettingKind.Bool:
                if (v is "0" or "1")
                    return true;
                reason = "expected 0 or 1";
                return false;
            case PlaylistSettingKind.Choice:
                if (Choices.Contains(v, StringComparer.Ordinal))
                    return true;
                reason = "not one of: " + string.Join(", ", Choices);
                return false;
            case PlaylistSettingKind.Weapon:
                if (v == SettingOptions.None || v.StartsWith("mp_weapon_", StringComparison.Ordinal))
                    return true;
                reason = "expected none or an mp_weapon_ class";
                return false;
            default:
            {
                var integer = Kind == PlaylistSettingKind.Int;
                if (!TryNumber(v, integer, out var n))
                {
                    reason = integer ? "expected a whole number" : "expected a number";
                    return false;
                }
                if (n < Min || n > Max)
                {
                    reason = string.Format(CultureInfo.InvariantCulture, "must be {0} to {1}", Min, Max);
                    return false;
                }
                return true;
            }
        }
    }

    public bool IsDefault(string? value) =>
        string.Equals(value?.Trim() ?? string.Empty, Default, StringComparison.Ordinal);

    public string RangeText => Kind switch
    {
        PlaylistSettingKind.Int or PlaylistSettingKind.Float =>
            string.Format(CultureInfo.InvariantCulture, "{0} - {1}", Min, Max),
        _ => string.Empty,
    };

    static bool TryNumber(string s, bool integer, out double value)
    {
        value = 0;
        if (integer)
        {
            if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                return false;
            value = l;
            return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    /// <summary>
    /// The overrides a dedi launch needs: every valid value that differs from
    /// the playlist default. Invalid values are dropped, never sent.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ToOverrides(
        IReadOnlyList<PlaylistSetting> settings,
        IReadOnlyDictionary<string, string>? values)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (values is null)
            return list;
        foreach (var s in settings)
        {
            if (!values.TryGetValue(s.Var, out var v) || s.IsDefault(v) || !s.Validate(v, out _))
                continue;
            list.Add(new KeyValuePair<string, string>(s.Var, v.Trim()));
        }

        // Server squad fill reads max_team_size. The client divides players by teams.
        // A players or teams change has to move the squad size with it, or the two disagree.
        if (HostCapacity.Conflict(settings, values) is not null)
        {
            list.RemoveAll(kv => HostCapacity.IsSlotVar(kv.Key));
            return list;
        }
        HostCapacity.AppendSquad(list, settings, values);
        return list;
    }
}

/// <summary>
/// A playlist that declares the force-legend switch also gets a legend picker. Scripts read
/// flowstateChosenCharacterRef; the choices are the legends the install's own script indexes.
/// </summary>
public static class ForcedLegend
{
    public const string ForceVar = "flowstateForceCharacter";
    public const string RefVar = "flowstateChosenCharacterRef";
    public const string RefPrefix = "character_";

    static readonly Regex s_refs = new(@"LEGEND_CHARACTER_REFS\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Compiled);
    static readonly Regex s_default = new(@"FS_1V1_DEFAULT_LEGEND_REF\s*=\s*""(?<ref>character_[a-z0-9_]+)""", RegexOptions.Compiled);
    static readonly Regex s_ref = new(@"""(character_[a-z0-9_]+)""", RegexOptions.Compiled);

    public static IReadOnlyList<PlaylistSetting> WithLegend(IReadOnlyList<PlaylistSetting> settings, string? installRoot)
    {
        if (!settings.Any(s => s.Var == ForceVar) || settings.Any(s => s.Var == RefVar))
            return settings;
        var (refs, def) = Legends(installRoot);
        if (refs.Count == 0)
            return settings;
        var list = settings.ToList();
        list.Insert(list.FindIndex(s => s.Var == ForceVar) + 1, new PlaylistSetting
        {
            Var = RefVar,
            Label = "Forced legend",
            Kind = PlaylistSettingKind.Choice,
            Choices = refs,
            Default = refs.Contains(def) ? def : refs[0],
        });
        return list;
    }

    static (IReadOnlyList<string> Refs, string Default) Legends(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return (Array.Empty<string>(), string.Empty);
        var path = Path.Combine(installRoot, "platform", "scripts", "vscripts", "gamemodes", "fs_1v1", "sh_fs_1v1_helpers.gnut");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
                return (Array.Empty<string>(), string.Empty);
            var text = File.ReadAllText(path);
            var body = s_refs.Match(text);
            if (!body.Success)
                return (Array.Empty<string>(), string.Empty);
            var refs = s_ref.Matches(body.Groups["body"].Value).Select(m => m.Groups[1].Value).Distinct().ToList();
            var def = s_default.Match(text);
            return (refs, def.Success ? def.Groups["ref"].Value : string.Empty);
        }
        catch (IOException)
        {
            return (Array.Empty<string>(), string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return (Array.Empty<string>(), string.Empty);
        }
    }
}

/// <summary>
/// Players and teams are on every mode. A playlist may narrow the range;
/// otherwise the slot limits apply (128 players, 126 teams).
/// </summary>
public static class HostCapacity
{
    public const string PlayersVar = "max_players";
    public const string TeamsVar = "max_teams";
    public const string SquadVar = "max_team_size";

    public const int PlayersMin = 1;
    public const int PlayersMax = 128;
    public const int TeamsMin = 1;
    public const int TeamsMax = 126;
    const int FallbackPlayers = 12;
    const int FallbackTeams = 2;

    public static bool IsSlotVar(string? var) =>
        var is not null && (
            var.Equals(PlayersVar, StringComparison.OrdinalIgnoreCase) ||
            var.Equals(TeamsVar, StringComparison.OrdinalIgnoreCase) ||
            var.Equals(SquadVar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Players, then teams, then whatever the mode itself declares.</summary>
    public static IReadOnlyList<PlaylistSetting> Pin(IReadOnlyList<PlaylistSetting> settings, string? playersRaw, string? teamsRaw)
    {
        var rest = new List<PlaylistSetting>(settings.Count);
        PlaylistSetting? players = null;
        PlaylistSetting? teams = null;
        foreach (var s in settings)
        {
            if (s.Var.Equals(PlayersVar, StringComparison.OrdinalIgnoreCase) && players is null)
                players = s;
            else if (s.Var.Equals(TeamsVar, StringComparison.OrdinalIgnoreCase) && teams is null)
                teams = s;
            else
                rest.Add(s);
        }

        players = WithUsableDefault(players ?? Builtin(PlayersVar, "Player count", PlayersMin, PlayersMax), playersRaw, FallbackPlayers);
        teams = WithUsableDefault(teams ?? Builtin(TeamsVar, "Team count", TeamsMin, TeamsMax), teamsRaw, FallbackTeams);
        rest.Insert(0, teams);
        rest.Insert(0, players);
        return rest;
    }

    /// <summary>Both numbers parsed and teams is larger than players. Null when the pair is usable or incomplete.</summary>
    public static string? Conflict(IReadOnlyList<PlaylistSetting> settings, IReadOnlyDictionary<string, string>? values)
    {
        if (!TryPair(settings, values, out var players, out var teams))
            return null;
        return teams > players ? "teams cannot be more than players" : null;
    }

    /// <summary>The player slot count a dedi should launch with. The playlist default when the pair does not fit.</summary>
    public static int SlotCount(IReadOnlyList<PlaylistSetting> settings, IReadOnlyDictionary<string, string>? values)
    {
        var setting = Find(settings, PlayersVar);
        if (setting is null)
            return 0;
        if (Conflict(settings, values) is null &&
            values is not null &&
            values.TryGetValue(setting.Var, out var chosen) &&
            setting.Validate(chosen, out _) &&
            int.TryParse(chosen.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        return setting.Validate(setting.Default, out _) &&
               int.TryParse(setting.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

    public static string? PerTeam(IReadOnlyList<PlaylistSetting> settings, IReadOnlyDictionary<string, string>? values)
    {
        if (Conflict(settings, values) is not null || !TryPair(settings, values, out var players, out var teams) || teams < 1)
            return null;
        return (players / teams).ToString(CultureInfo.InvariantCulture) + " per team";
    }

    public static void AppendSquad(
        List<KeyValuePair<string, string>> list,
        IReadOnlyList<PlaylistSetting> settings,
        IReadOnlyDictionary<string, string> values)
    {
        if (Find(settings, SquadVar) is not null)
            return;
        var players = Find(settings, PlayersVar);
        var teams = Find(settings, TeamsVar);
        if (players is null || teams is null)
            return;
        if (!TryValue(players, values, out var playerText, out var playerCount) ||
            !TryValue(teams, values, out var teamText, out var teamCount) ||
            teamCount < 1 || teamCount > playerCount)
            return;
        if (players.IsDefault(playerText) && teams.IsDefault(teamText))
            return;

        var squad = (playerCount / teamCount).ToString(CultureInfo.InvariantCulture);
        list.RemoveAll(kv => kv.Key.Equals(SquadVar, StringComparison.OrdinalIgnoreCase));
        list.Add(new KeyValuePair<string, string>(SquadVar, squad));
    }

    static bool TryPair(IReadOnlyList<PlaylistSetting> settings, IReadOnlyDictionary<string, string>? values, out int players, out int teams)
    {
        players = 0;
        teams = 0;
        var playerSetting = Find(settings, PlayersVar);
        var teamSetting = Find(settings, TeamsVar);
        if (playerSetting is null || teamSetting is null || values is null)
            return false;
        return TryValue(playerSetting, values, out _, out players) &&
               TryValue(teamSetting, values, out _, out teams);
    }

    static bool TryValue(PlaylistSetting setting, IReadOnlyDictionary<string, string> values, out string text, out int number)
    {
        text = values.TryGetValue(setting.Var, out var chosen) ? chosen.Trim() : setting.Default;
        if (!setting.Validate(text, out _))
            text = setting.Default;
        number = 0;
        return setting.Validate(text, out _) &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    static PlaylistSetting? Find(IReadOnlyList<PlaylistSetting> settings, string var)
    {
        foreach (var s in settings)
        {
            if (s.Var.Equals(var, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    static PlaylistSetting Builtin(string var, string label, int min, int max) => new()
    {
        Var = var,
        Label = label,
        Kind = PlaylistSettingKind.Int,
        Min = min,
        Max = max,
    };

    static PlaylistSetting WithUsableDefault(PlaylistSetting setting, string? raw, int fallback)
    {
        if (setting.Validate(setting.Default, out _))
            return setting;
        var candidate = raw?.Trim() ?? string.Empty;
        if (!setting.Validate(candidate, out _))
        {
            var n = fallback;
            if (n < setting.Min)
                n = (int)setting.Min;
            if (n > setting.Max)
                n = (int)setting.Max;
            candidate = n.ToString(CultureInfo.InvariantCulture);
            if (!setting.Validate(candidate, out _))
                return setting;
        }
        return new PlaylistSetting
        {
            Var = setting.Var,
            Label = setting.Label,
            Kind = setting.Kind,
            Min = setting.Min,
            Max = setting.Max,
            Choices = setting.Choices,
            Default = candidate,
        };
    }
}
