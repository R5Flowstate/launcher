using System.Text.RegularExpressions;

namespace R5Flowstate.Contracts;

/// <summary>
/// Which files a mod may ship. Mirrors the engine's rule: the game falls back to
/// mod folders for any file the base install lacks, so a mod may only carry the
/// fixed manifest files, names carrying its namespace, and the files of maps it
/// declares. Anything else could stand in for a missing product file.
/// </summary>
public static class ModOwnership
{
    public const int MaxMaps = 16;
    public const int MaxOverrides = 64;

    /// <summary>No shipped file name contains it, so "&lt;namespace&gt;__..." is never a game file.</summary>
    public const string Separator = "__";

    // Our own trust UI (EULA, mod policy, launcher handoff) reads the same with any mod,
    // and so do the dialog buttons that answer it.
    static readonly string[] ProtectedLocPrefixes = { "SDK_", "BRIDGE_", "EULA" };
    static readonly string[] ProtectedLocTokens = { "YES", "NO", "OK", "CANCEL" };

    static readonly HashSet<string> FixedFiles = new(StringComparer.Ordinal)
    {
        "mod.vdf", "manifest.json", "icon.png", "readme.md", "changelog.md",
        "license", "license.md", "license.txt",
        "scripts/vscripts/scripts.rson", "paks/win64/preload.rson", "paks/win64_server/preload.rson",
        "playlists_r5_patch.txt",
    };

    static readonly Regex s_mapChars = new(
        "^[a-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Lowercase id with '.' folded to '_', as the engine keys it.</summary>
    public static string Namespace(string id) => ModId.Normalize(id).ToLowerInvariant();

    /// <summary>A namespace containing the separator would make its names ambiguous.</summary>
    public static bool IsUsableNamespace(string nameSpace) =>
        nameSpace.Length > 0 && !nameSpace.Contains(Separator, StringComparison.Ordinal);

    /// <summary>Declared maps from the <c>Maps</c> block, or null with a reason.</summary>
    public static IReadOnlyList<string>? ReadMaps(ModVdfDocument doc, string nameSpace, out string error)
    {
        error = string.Empty;
        var prefix = "mp_" + nameSpace + Separator;
        var maps = new List<string>();
        foreach (var entry in BlockEntries(doc, "Maps"))
        {
            var map = entry.ToLowerInvariant();
            if (map.Length <= prefix.Length || map.Length > 63 ||
                !map.StartsWith(prefix, StringComparison.Ordinal) || !s_mapChars.IsMatch(map))
            {
                error = "map '" + entry + "' must be named " + prefix + "<name> (letters, digits, '_', at most 63)";
                return null;
            }

            if (maps.Count >= MaxMaps)
            {
                error = "more than " + MaxMaps + " maps declared";
                return null;
            }

            maps.Add(map);
        }

        return maps;
    }

    public static bool IsProtectedLocKey(string key)
    {
        var name = key.StartsWith('#') ? key[1..] : key;
        return ProtectedLocTokens.Any(t => string.Equals(name, t, StringComparison.OrdinalIgnoreCase))
               || ProtectedLocPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Stock tables the mod replaces: <c>"DatatableOverrides" { "survival_loot" "1" }</c>.</summary>
    public static IReadOnlyList<string>? ReadDatatableOverrides(ModVdfDocument doc, out string error)
    {
        error = string.Empty;
        var tables = new List<string>();
        foreach (var entry in BlockEntries(doc, "DatatableOverrides"))
        {
            var table = entry.ToLowerInvariant();
            var slash = table.IndexOf('/');
            var ok = table.Length is > 0 and <= 96 && table.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '/')
                     && slash != 0 && (slash < 0 || (slash < table.Length - 1 && table.IndexOf('/', slash + 1) < 0));
            if (!ok || tables.Count >= MaxOverrides)
            {
                error = "invalid or excess datatable override '" + entry + "'";
                return null;
            }

            tables.Add(table);
        }

        return tables;
    }

    /// <summary>Stock strings the mod replaces: <c>"LocalizationOverrides" { "WPN_R99" "1" }</c>.</summary>
    public static IReadOnlyList<string>? ReadLocalizationOverrides(ModVdfDocument doc, out string error)
    {
        error = string.Empty;
        var tokens = new List<string>();
        foreach (var entry in BlockEntries(doc, "LocalizationOverrides"))
        {
            var token = entry.StartsWith('#') ? entry[1..] : entry;
            var ok = token.Length is > 0 and <= 96 && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
            if (!ok || IsProtectedLocKey(token) || tokens.Count >= MaxOverrides)
            {
                error = "mods may not override localization token '" + entry + "'";
                return null;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// True when a declared localization override names the mod's own text (its namespace or a
    /// map it adds), so it cannot change anything the game ships.
    /// </summary>
    public static bool OwnsLocKey(string nameSpace, IReadOnlyList<string> maps, string token)
    {
        var key = token.ToLowerInvariant();
        if (nameSpace.Length > 0 && key.StartsWith(nameSpace + Separator, StringComparison.Ordinal))
            return true;
        if (key.StartsWith("mp_" + nameSpace + Separator, StringComparison.Ordinal))
            return true;
        return maps.Any(m => key == m || key.StartsWith(m + "_", StringComparison.Ordinal));
    }

    static IEnumerable<string> BlockEntries(ModVdfDocument doc, string name) =>
        doc.Blocks
            .Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(b => b.Entries.Select(e => e.Key));

    /// <summary>
    /// <paramref name="installPath"/> lets a folder named exactly after the namespace
    /// count only where the game has no folder of that name (id "weapons" vs scripts/weapons).
    /// </summary>
    public static bool OwnsPath(
        string nameSpace,
        IReadOnlyList<string> maps,
        IReadOnlyList<string> datatableOverrides,
        string relative,
        string? installPath)
    {
        var rel = relative.Replace('\\', '/').ToLowerInvariant();
        if (FixedFiles.Contains(rel))
            return true;
        if (datatableOverrides.Any(t => rel == "datatable/" + t + ".csv"))
            return true;

        var parts = rel.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var comp = parts[i];
            if (HasNamespace(comp, nameSpace))
                return true;

            var isDir = i < parts.Length - 1;
            if (isDir && comp == nameSpace && !BaseHasDirectory(installPath, parts, i))
                return true;

            foreach (var map in maps)
            {
                if (EmbedsMap(comp, map))
                    return true;
            }
        }

        return false;
    }

    static bool HasNamespace(string comp, string nameSpace) =>
        nameSpace.Length > 0 &&
        comp.Length > nameSpace.Length + Separator.Length &&
        comp.StartsWith(nameSpace + Separator, StringComparison.Ordinal);

    static bool BaseHasDirectory(string? installPath, string[] parts, int index)
    {
        if (string.IsNullOrEmpty(installPath))
            return false;
        var dir = Path.Combine(parts.Take(index + 1).ToArray());
        return Directory.Exists(Path.Combine(installPath, dir)) ||
               Directory.Exists(Path.Combine(installPath, "platform", dir));
    }

    static bool LeadsWith(string comp, string token) =>
        comp.StartsWith(token, StringComparison.Ordinal) &&
        (comp.Length == token.Length || comp[token.Length] is '_' or '.');

    // VPKs carry the map after a prefix: client_mp_foo.bsp.pak000_dir.vpk.
    static bool EmbedsMap(string comp, string map)
    {
        if (LeadsWith(comp, map))
            return true;
        for (var i = 1; i + map.Length < comp.Length; i++)
        {
            if (comp[i - 1] != '_' || string.CompareOrdinal(comp, i, map, 0, map.Length) != 0)
                continue;
            if (comp[i + map.Length] is '_' or '.')
                return true;
        }

        return false;
    }
}
