using System.Globalization;
using System.Text.RegularExpressions;

namespace R5Flowstate.Spawn;

/// <summary>One entry of a host-setting dropdown: the value sent to the server and its label.</summary>
public sealed record SettingOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Dropdown entries for choice and weapon settings, labelled from the install.</summary>
public static class SettingOptions
{
    public const string None = "none";

    public static IReadOnlyList<SettingOption> For(PlaylistSetting s, string? installRoot, string language = "english")
    {
        switch (s.Kind)
        {
            case PlaylistSettingKind.Choice:
            {
                var names = WeaponCatalog.Names(installRoot, s.Choices, language);
                return s.Choices.Select(c => new SettingOption(c, LabelFor(c, names))).ToList();
            }
            case PlaylistSettingKind.Weapon:
            {
                var list = new List<SettingOption> { new(None, "None") };
                list.AddRange(WeaponCatalog.MainWeapons(installRoot, language));
                if (s.Default.Length > 0 && list.All(o => o.Value != s.Default))
                    list.Add(new SettingOption(s.Default, s.Default));
                return list;
            }
            default:
                return Array.Empty<SettingOption>();
        }
    }

    static string LabelFor(string value, IReadOnlyDictionary<string, string> weaponNames)
    {
        if (value == None)
            return "None";
        if (weaponNames.TryGetValue(value, out var name))
            return name;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    }
}

/// <summary>
/// Weapons the install can hand out, read from its own loot table and weapon scripts:
/// every enabled main weapon that is not an attachment kit or care-package variant.
/// </summary>
public static class WeaponCatalog
{
    static readonly Regex s_printName = new("\"printname\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
    static readonly object s_lock = new();
    static string _cacheKey = string.Empty;
    static IReadOnlyList<SettingOption> _cache = Array.Empty<SettingOption>();

    public static IReadOnlyList<SettingOption> MainWeapons(string? installRoot, string language = "english")
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return Array.Empty<SettingOption>();

        var csv = Path.Combine(installRoot, "platform", "datatable", "survival_loot.csv");
        string key;
        try
        {
            var info = new FileInfo(csv);
            if (!info.Exists)
                return Array.Empty<SettingOption>();
            key = csv + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + language;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<SettingOption>();
        }

        lock (s_lock)
        {
            if (key == _cacheKey)
                return _cache;
        }

        var refs = ReadBaseMainWeapons(csv);
        var names = Names(installRoot, refs, language);
        var list = refs
            .Select(r => new SettingOption(r, names.TryGetValue(r, out var n) ? n : r))
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (s_lock)
        {
            _cacheKey = key;
            _cache = list;
        }
        return list;
    }

    /// <summary>Display names for weapon refs, from each weapon script's printname.</summary>
    public static IReadOnlyDictionary<string, string> Names(string? installRoot, IEnumerable<string> refs, string language = "english")
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(installRoot))
            return result;

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in refs)
        {
            if (!r.StartsWith("mp_weapon_", StringComparison.OrdinalIgnoreCase) || r.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                continue;
            try
            {
                var path = Path.Combine(installRoot, "platform", "scripts", "weapons", r + ".txt");
                if (!File.Exists(path))
                    continue;
                foreach (var line in File.ReadLines(path).Take(64))
                {
                    var m = s_printName.Match(line);
                    if (m.Success)
                    {
                        tokens[r] = m.Groups[1].Value;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        var loc = PlaylistCatalogLoader.ResolveLocTokens(installRoot, tokens.Values, language);
        foreach (var (r, token) in tokens)
        {
            if (loc.TryGetValue(token, out var name) && name.Length > 0)
                result[r] = name;
        }
        return result;
    }

    static List<string> ReadBaseMainWeapons(string csvPath)
    {
        var refs = new List<string>();
        try
        {
            using var reader = new StreamReader(csvPath);
            var header = reader.ReadLine();
            if (header is null)
                return refs;
            var cols = SplitCsvLine(header);
            int iDisabled = cols.IndexOf("isDisabled"), iRef = cols.IndexOf("ref"),
                iType = cols.IndexOf("type"), iBase = cols.IndexOf("baseWeapon");
            if (iRef < 0 || iType < 0 || iBase < 0)
                return refs;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var f = SplitCsvLine(line);
                if (f.Count <= Math.Max(iRef, Math.Max(iType, iBase)))
                    continue;
                var r = f[iRef];
                if (f[iType] != "main_weapon" || f[iBase].Length != 0 ||
                    (iDisabled >= 0 && iDisabled < f.Count && f[iDisabled].Equals("true", StringComparison.OrdinalIgnoreCase)) ||
                    !r.StartsWith("mp_weapon_", StringComparison.Ordinal) ||
                    r.EndsWith("_crate", StringComparison.Ordinal) || refs.Contains(r))
                    continue;
                refs.Add(r);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return refs;
    }

    static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    sb.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
                sb.Append(c);
        }
        fields.Add(sb.ToString().Trim());
        return fields;
    }
}
