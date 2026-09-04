using System.Text.RegularExpressions;

namespace R5Flowstate.Spawn;

/// <summary>One launchable playlist from the Playlists { } section.</summary>
public sealed class PlaylistEntry
{
    /// <summary>Wire id for +launchplaylist (e.g. survival_dev).</summary>
    public required string Id { get; init; }

    /// <summary>Raw name var: #PL_FREE_ROAM, "literal", or empty.</summary>
    public string? NameKey { get; init; }

    /// <summary>Localized or fallback display label (includes id for Advanced).</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Resolved label alone for mode cards; falls back to id.</summary>
    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> Maps { get; init; } = Array.Empty<string>();

    /// <summary>vars r5f_mode 1 -- offer this playlist as a player-facing mode.</summary>
    public bool IsMode { get; init; }

    /// <summary>vars r5f_overlay 1 -- overlay + Advanced combo, not a Simple mode card.</summary>
    public bool IsOverlay { get; init; }

    /// <summary>vars r5f_mode_order -- ascending sort key for the mode list.</summary>
    public int ModeOrder { get; init; }

    /// <summary>vars r5f_mode_map -- the one map this mode runs; empty means the
    /// player chooses. Firing Range pins its arena because the mode is the arena.</summary>
    public string PinnedMap { get; init; } = string.Empty;

    /// <summary>vars r5f_mode_map_title -- label for this entry's map inside its
    /// family ("Habitat" under TDM). Empty falls back to the map's own name.</summary>
    public string MapTitle { get; init; } = string.Empty;

    /// <summary>One-line blurb from the r5f_mode_blurb var; may be empty.</summary>
    public string Blurb { get; init; } = string.Empty;

    /// <summary>vars r5f_mode_group -- "apex" for a retail mode. Anything else,
    /// including absent, is ours: retail is a closed set we tag once, so a mode
    /// nobody classified is a Flowstate mode by construction.</summary>
    public string Group { get; init; } = ModeGroups.Flowstate;

    /// <summary>vars r5f_mode_family -- groups the mode-per-map entries Apex ships
    /// (TDM Habitat, TDM Estates, ...) under one player-facing mode. Empty means
    /// the entry is a family of one.</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>vars r5f_mode_family_title / _order.</summary>
    public string FamilyTitle { get; init; } = string.Empty;
    public int FamilyOrder { get; init; }

    /// <summary>vars r5f_mode_icon -- rail glyph name. Empty means the family id,
    /// which is what every mode shipped so far uses.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>vars r5f_mode_family_blurb -- describes the whole family, so it is
    /// set once rather than repeated on all seven of TDM's per-map entries.</summary>
    public string FamilyBlurb { get; init; } = string.Empty;

    // DisplayName already includes "Label  (id)" when localized.
    public override string ToString() =>
        string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

public static class ModeGroups
{
    public const string Apex = "apex";
    public const string Flowstate = "flowstate";

    /// <summary>Rail order. Apex is the bulk of the list, so it reads first.</summary>
    public static readonly IReadOnlyList<string> Order = new[] { Apex, Flowstate };

    public static string Normalize(string? raw) =>
        string.Equals(raw?.Trim(), Apex, StringComparison.OrdinalIgnoreCase)
            ? Apex
            : Flowstate;
}

/// <summary>One map a family can be played on, and the playlist that runs it.</summary>
public sealed class ModeVariant
{
    public required string PlaylistId { get; init; }
    public required string MapStem { get; init; }

    /// <summary>Label from r5f_mode_map_title; empty means use the map's own name.</summary>
    public string MapTitle { get; init; } = string.Empty;
}

/// <summary>
/// One player-facing mode. Apex ships these as one playlist per map, so a family
/// usually has many variants over many playlist ids; our own modes ship as one
/// playlist listing many maps, so a family has many variants over one id. The
/// picker cannot tell the difference, which is the point.
/// </summary>
public sealed class ModeFamily
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string Blurb { get; init; } = string.Empty;
    public int Order { get; init; }
    public IReadOnlyList<ModeVariant> Variants { get; init; } = Array.Empty<ModeVariant>();

    /// <summary>Rail glyph name; falls back to the family id.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Title came from r5f_mode_family_title, so it is written the way it
    /// should read. A title recovered from the playlist name is not -- localization
    /// stores those shouted -- and only that one needs case repair.</summary>
    public bool TitleIsAuthored { get; init; }

    /// <summary>The mode is its own map, so there is nothing to pick.</summary>
    public bool MapIsPinned => Variants.Count <= 1;

    public bool IsApex => string.Equals(Group, ModeGroups.Apex, StringComparison.Ordinal);

    public ModeVariant? FindVariant(string? mapStem) =>
        string.IsNullOrWhiteSpace(mapStem)
            ? null
            : Variants.FirstOrDefault(v =>
                  string.Equals(v.MapStem, mapStem.Trim(), StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Title;
}

public sealed class PlaylistCatalog
{
    public string? SourcePath { get; init; }
    public string? LocalizationPath { get; init; }
    public IReadOnlyList<PlaylistEntry> Entries { get; init; } = Array.Empty<PlaylistEntry>();
    public IReadOnlyList<string> AllMaps { get; init; } = Array.Empty<string>();

    /// <summary>Map stems actually present in the install. A build ships a curated
    /// subset, so the playlist can name maps this install does not have.</summary>
    public IReadOnlyList<string> MapsOnDisk { get; init; } = Array.Empty<string>();

    /// <summary>Curated stems in file order -- the allowlist of maps a player may
    /// pick. Empty when the install ships no map-names file.</summary>
    public IReadOnlyList<string> MapOrder { get; init; } = Array.Empty<string>();

    /// <summary>stem -> human name from platform/r5f_map_names.txt plus
    /// r5f_wip_maps.txt (ship names win on a collision); may be empty.</summary>
    public IReadOnlyDictionary<string, string> MapNames { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stems listed in platform/r5f_wip_maps.txt, file order. Offered
    /// in addition to the ship allowlist when the files are on disk.</summary>
    public IReadOnlyList<string> WipOrder { get; init; } = Array.Empty<string>();

    /// <summary>When set, Curate also keeps on-disk stems that missed the ship
    /// allowlist. Default off so the player picker stays the curated list.</summary>
    public bool IncludeUnlistedMaps { get; init; }

    /// <summary>Marked modes, in r5f_mode_order then display order.</summary>
    public IReadOnlyList<PlaylistEntry> Modes { get; init; } = Array.Empty<PlaylistEntry>();

    /// <summary>Modes collapsed by r5f_mode_family, grouped Apex then Flowstate.</summary>
    public IReadOnlyList<ModeFamily> Families { get; internal set; } = Array.Empty<ModeFamily>();

    /// <summary>
    /// Maps that already have a mode of their own (Firing Range's arena), so a
    /// free-choice picker should not offer them again. Only families of ONE
    /// qualify: a TDM map is pinned by its playlist but is still just a map.
    /// </summary>
    public IReadOnlyCollection<string> SoloPinnedMaps { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ModeFamily? FindFamily(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Families.FirstOrDefault(f =>
                  string.Equals(f.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ModeFamily> FamiliesInGroup(string group) =>
        Families.Where(f => string.Equals(f.Group, group, StringComparison.Ordinal)).ToList();

    /// <summary>r5f_mode or r5f_overlay, in r5f_mode_order. Advanced dedi combo.</summary>
    public IReadOnlyList<PlaylistEntry> HostPlaylists { get; init; } = Array.Empty<PlaylistEntry>();

    /// <summary>Ids only (compat).</summary>
    public IReadOnlyList<string> Playlists => Entries.Select(e => e.Id).ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> MapsByPlaylist { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public PlaylistEntry? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return Entries.FirstOrDefault(e =>
            string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> MapsForPlaylist(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist))
            return AllMaps;
        if (MapsByPlaylist.TryGetValue(playlist.Trim(), out var maps) && maps.Count > 0)
            return maps;
        return AllMaps;
    }

    /// <summary>
    /// Reduce a declared map list to what a player should actually be offered:
    /// the curated allowlist when the install ships one, then what is on disk.
    /// Allowlist order wins, so the file also decides how the picker reads.
    /// Either filter emptying the list means the filter is wrong for this
    /// install, so the declared list stands rather than an empty picker.
    /// WIP stems (r5f_wip_maps.txt) and, when IncludeUnlistedMaps is set,
    /// other on-disk stems are appended after that list -- they never rewrite it.
    /// </summary>
    private IReadOnlyList<string> Curate(IReadOnlyList<string> stems)
    {
        var declared = stems;
        var kept = stems;

        if (MapOrder.Count > 0)
        {
            var byAllow = MapOrder.Where(m => kept.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (byAllow.Count > 0)
                kept = byAllow;
        }

        HashSet<string>? disk = null;
        if (MapsOnDisk.Count > 0)
        {
            disk = new HashSet<string>(MapsOnDisk, StringComparer.OrdinalIgnoreCase);
            var onDisk = kept.Where(disk.Contains).ToList();
            if (onDisk.Count > 0)
                kept = onDisk;
        }

        var have = new HashSet<string>(kept, StringComparer.OrdinalIgnoreCase);
        var declaredSet = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);
        var extras = new List<string>();

        bool Eligible(string m)
        {
            if (!declaredSet.Contains(m))
                return false;
            if (disk is not null && !disk.Contains(m))
                return false;
            return have.Add(m);
        }

        foreach (var m in WipOrder)
        {
            if (Eligible(m))
                extras.Add(m);
        }

        if (IncludeUnlistedMaps)
        {
            foreach (var m in disk is not null ? MapsOnDisk : declared)
            {
                if (Eligible(m))
                    extras.Add(m);
            }
        }

        if (extras.Count == 0)
            return kept;
        return kept.Concat(extras).ToList();
    }

    /// <summary>Maps to offer for a mode card: its own maps{} when it declares a
    /// real choice, otherwise every map found on disk. A stem that already has
    /// its own button (Firing Range via r5f_mode_map, Lobby via the synthetic
    /// card) is dropped -- those modes are not a Free Roam setting.</summary>
    public IReadOnlyList<string> MapChoicesForMode(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            return ExcludeOwnButtonMaps(Curate(AllMaps), playlistId);
        var entry = Find(playlistId);
        if (entry is null)
            return ExcludeOwnButtonMaps(Curate(AllMaps), playlistId);
        if (!string.IsNullOrWhiteSpace(entry.PinnedMap))
            return new[] { entry.PinnedMap };
        if (entry.Maps.Count >= 2)
            return ExcludeOwnButtonMaps(Curate(entry.Maps), playlistId);
        return ExcludeOwnButtonMaps(Curate(AllMaps), playlistId);
    }

    private IReadOnlyList<string> ExcludeOwnButtonMaps(IReadOnlyList<string> stems, string? playlistId)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in Modes)
        {
            if (string.IsNullOrWhiteSpace(mode.PinnedMap))
                continue;
            if (!SoloPinnedMaps.Contains(mode.PinnedMap))
                continue;
            if (!string.IsNullOrWhiteSpace(playlistId) &&
                string.Equals(mode.Id, playlistId, StringComparison.OrdinalIgnoreCase))
                continue;
            reserved.Add(mode.PinnedMap);
        }

        // Lobby is a synthetic card, not a playlist mode, so it never has a PinnedMap.
        if (!string.Equals(playlistId, "lobby", StringComparison.OrdinalIgnoreCase))
            reserved.Add("mp_lobby");

        if (reserved.Count == 0)
            return stems;

        var kept = stems.Where(s => !reserved.Contains(s)).ToList();
        return kept.Count > 0 ? kept : stems;
    }
}

public static class PlaylistCatalogLoader
{
    // Apex level stems are mp_rr_* (and rare specials). Never mp_ability_* / mp_weapon_*.
    private static readonly Regex s_mapToken = new(
        @"^(mp_rr_[a-zA-Z0-9_]+|mp_lobby)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Structural / non-playlist block names under Playlists { }
    private static readonly HashSet<string> s_skipBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        "vars", "gamemodes", "maps", "inherit", "defaults", "Playlists", "Gamemodes",
        "playlists", "rotation", "rotationStartTime", "start",
    };

    // Never treat these as launchable playlist ids (containers / noise).
    private static readonly HashSet<string> s_rejectId = new(StringComparer.OrdinalIgnoreCase)
    {
        "PlaylistRotation", "PlaylistSchedule",
        "Gamemodes", "Playlists", "playlists", "defaults", "vars", "maps", "gamemodes",
    };

    // Playlists with no local-play button. Hosting one is a normal thing to do --
    // the launcher spawns a dedi and connects to it -- so this stays empty and the
    // list exists only as the lever for a mode that genuinely cannot be hosted.
    private static readonly HashSet<string> s_multiplayerOnlyModes = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindPlaylistFile(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return null;

        string root;
        try { root = Path.GetFullPath(installRoot); }
        catch { return null; }

        var candidates = new[]
        {
            Path.Combine(root, "platform", "playlists_r5_patch.txt"),
            Path.Combine(root, "r2", "playlists_r5.txt"),
            Path.Combine(root, "platform", "playlists_r5.txt"),
            Path.Combine(root, "playlists_r5_patch.txt"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    public static string? FindLocalizationFile(string installRoot, string language = "english")
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return null;

        string root;
        try { root = Path.GetFullPath(installRoot); }
        catch { return null; }

        var path = Path.Combine(root, "platform", "localization", $"localization_{language}.txt");
        return File.Exists(path) ? path : null;
    }

    public static PlaylistCatalog Load(
        string installRoot,
        string? language = null,
        bool includeUnlistedMaps = false)
    {
        var path = FindPlaylistFile(installRoot);
        var lang = string.IsNullOrWhiteSpace(language) ? "english" : language.Trim();
        var locPath = FindLocalizationFile(installRoot, lang)
            ?? FindLocalizationFile(installRoot, "english");
        var entries = new Dictionary<string, PlaylistEntryBuilder>(StringComparer.OrdinalIgnoreCase);
        var allMaps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (path is not null)
        {
            try
            {
                ParsePlaylistsSection(File.ReadAllText(path), entries, allMaps);
            }
            catch
            {
                // keep empty
            }
        }

        var loc = LoadLocalization(locPath, CollectLocNeedles(entries.Values));

        var onDisk = DiscoverMapsOnDisk(installRoot).ToList();
        var curation = LoadMapCuration(installRoot);
        var wip = LoadWipCuration(installRoot);
        foreach (var m in onDisk)
            allMaps.Add(m);

        var list = entries.Values
            .Select(b => b.Build(loc, entries))
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byPl = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in list)
            byPl[e.Id] = e.Maps;

        var modes = list
            .Where(e => e.IsMode && !s_multiplayerOnlyModes.Contains(e.Id))
            .OrderBy(e => e.ModeOrder)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var host = list
            .Where(e => e.IsMode || e.IsOverlay)
            .OrderBy(e => e.ModeOrder)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var catalog = new PlaylistCatalog
        {
            SourcePath = path,
            LocalizationPath = locPath,
            Entries = list,
            AllMaps = allMaps.ToList(),
            MapsOnDisk = onDisk,
            MapOrder = curation.Order,
            MapNames = MergeMapNames(curation.Names, wip.Names),
            WipOrder = wip.Order,
            IncludeUnlistedMaps = includeUnlistedMaps,
            MapsByPlaylist = byPl,
            Modes = modes,
            HostPlaylists = host,
            SoloPinnedMaps = SoloPinnedMapsOf(modes),
        };

        catalog.Families = BuildFamilies(catalog);
        return catalog;
    }

    /// <summary>Pinned maps belonging to a mode that is alone in its family.</summary>
    private static HashSet<string> SoloPinnedMapsOf(IReadOnlyList<PlaylistEntry> modes)
    {
        var perFamily = modes
            .GroupBy(FamilyKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .SelectMany(g => g)
            .Where(e => !string.IsNullOrWhiteSpace(e.PinnedMap))
            .Select(e => e.PinnedMap);
        return new HashSet<string>(perFamily, StringComparer.OrdinalIgnoreCase);
    }

    private static string FamilyKey(PlaylistEntry e) =>
        string.IsNullOrWhiteSpace(e.Family) ? e.Id : e.Family.Trim();

    /// <summary>
    /// Collapse Modes into families. Every member contributes its own map choices
    /// as variants pointing at its own playlist id, so a family split across many
    /// playlists and a family listing many maps in one playlist come out the same
    /// shape. A map claimed twice keeps the first member in family order.
    /// </summary>
    private static IReadOnlyList<ModeFamily> BuildFamilies(PlaylistCatalog catalog)
    {
        var families = new List<ModeFamily>();

        foreach (var grouped in catalog.Modes.GroupBy(FamilyKey, StringComparer.OrdinalIgnoreCase))
        {
            var members = grouped
                .OrderBy(e => e.FamilyOrder != 0 ? e.FamilyOrder : e.ModeOrder)
                .ThenBy(e => e.ModeOrder)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var head = members[0];
            var variants = new List<ModeVariant>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var member in members)
            {
                foreach (var stem in catalog.MapChoicesForMode(member.Id))
                {
                    if (!claimed.Add(stem))
                        continue;
                    variants.Add(new ModeVariant
                    {
                        PlaylistId = member.Id,
                        MapStem = stem,
                        // Only meaningful when the member IS the map; a member that
                        // offers a whole map list has one title and many maps.
                        MapTitle = members.Count > 1 ? member.MapTitle : string.Empty,
                    });
                }
            }

            if (variants.Count == 0)
                continue;

            var authored = FirstNonEmpty(members.Select(m => m.FamilyTitle));
            var title = authored
                        ?? FirstNonEmpty(members.Select(m => m.Title))
                        ?? grouped.Key;

            families.Add(new ModeFamily
            {
                Id = grouped.Key,
                Group = head.Group,
                Title = title,
                TitleIsAuthored = authored is not null,
                Icon = FirstNonEmpty(members.Select(m => m.Icon)) ?? grouped.Key,
                Blurb = FirstNonEmpty(members.Select(m => m.FamilyBlurb))
                        ?? FirstNonEmpty(members.Select(m => m.Blurb)) ?? string.Empty,
                Order = members.Select(m => m.FamilyOrder != 0 ? m.FamilyOrder : m.ModeOrder)
                               .DefaultIfEmpty(0).Min(),
                Variants = variants,
            });
        }

        return families
            .OrderBy(f => ModeGroups.Order.ToList().IndexOf(f.Group))
            .ThenBy(f => f.Order)
            .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FirstNonEmpty(IEnumerable<string?> values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>
    /// platform/r5f_map_names.txt plus r5f_wip_maps.txt. Ship names win on a clash.
    /// Absent files are normal; callers fall back to the stem alone.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadMapNames(string installRoot) =>
        MergeMapNames(LoadMapCuration(installRoot).Names, LoadWipCuration(installRoot).Names);

    /// <summary>
    /// platform/r5f_map_names.txt -- "stem = Human Name" per line, # or // comments.
    /// Every listed stem is offered to the player, in file order; a stem left out
    /// is hidden. A blank name shows the stem alone. Absent file = no allowlist.
    /// </summary>
    public static (IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Names)
        LoadMapCuration(string installRoot) =>
        LoadStemNameFile(installRoot, "r5f_map_names.txt");

    /// <summary>
    /// platform/r5f_wip_maps.txt -- same format as the ship names file.
    /// Listed stems are offered in addition to the allowlist when they are on disk.
    /// Not overlay-owned; a player pack update must not wipe it.
    /// </summary>
    public static (IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Names)
        LoadWipCuration(string installRoot) =>
        LoadStemNameFile(installRoot, "r5f_wip_maps.txt");

    static IReadOnlyDictionary<string, string> MergeMapNames(
        IReadOnlyDictionary<string, string> ship,
        IReadOnlyDictionary<string, string> wip)
    {
        if (wip.Count == 0)
            return ship;
        var merged = new Dictionary<string, string>(ship, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in wip)
        {
            if (!merged.ContainsKey(kv.Key))
                merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    static (IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Names)
        LoadStemNameFile(string installRoot, string fileName)
    {
        var order = new List<string>();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(fileName))
            return (order, dict);

        string path;
        try { path = Path.Combine(Path.GetFullPath(installRoot), "platform", fileName); }
        catch { return (order, dict); }

        if (!File.Exists(path))
            return (order, dict);

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                    continue;
                // A trailing '=' with no name is a listed map shown by stem alone.
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var stem = line[..eq].Trim();
                var name = eq + 1 < line.Length ? line[(eq + 1)..].Trim() : string.Empty;
                if (stem.Length == 0)
                    continue;
                if (!dict.ContainsKey(stem) && !order.Contains(stem, StringComparer.OrdinalIgnoreCase))
                    order.Add(stem);
                if (name.Length > 0)
                    dict[stem] = name;
            }
        }
        catch
        {
            // a partial list is still better than none
        }

        return (order, dict);
    }

    public static IEnumerable<string> DiscoverMapsOnDisk(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            yield break;

        string root;
        try { root = Path.GetFullPath(installRoot); }
        catch { yield break; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packed = new[]
        {
            Path.Combine(root, "vpk"),
            Path.Combine(root, "paks", "Win64_server"),
            Path.Combine(root, "paks", "Win64"),
        };
        var loose = new[]
        {
            Path.Combine(root, "maps"),
            Path.Combine(root, "platform", "maps"),
            Path.Combine(root, "r2", "maps"),
        };

        foreach (var dir in packed)
        {
            foreach (var stem in StemsInDir(dir, SearchOption.TopDirectoryOnly, seen))
                yield return stem;
        }

        foreach (var dir in loose)
        {
            foreach (var stem in StemsInDir(dir, SearchOption.AllDirectories, seen))
                yield return stem;
        }
    }

    static IEnumerable<string> StemsInDir(
        string dir, SearchOption opt, HashSet<string> seen)
    {
        if (!Directory.Exists(dir))
            yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "mp_rr_*", opt)
                .Concat(Directory.EnumerateFiles(dir, "mp_lobby*", opt));
        }
        catch { yield break; }

        foreach (var f in files)
        {
            if (PathIsNavmesh(f))
                continue;

            var name = Path.GetFileName(f);
            if (name.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("prededi", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("loadscreen", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("client_perm", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("client_temp", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".nm", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".starpak", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".opt.starpak", StringComparison.OrdinalIgnoreCase))
                continue;

            var stem = TryMapStemFromFileName(name);
            if (stem is null)
                continue;
            if (seen.Add(stem))
                yield return stem;
        }
    }

    /// <summary>
    /// Recast hulls live at maps/navmesh/&lt;stem&gt;_small.nm (also medium,
    /// large, extra_large, med_short) plus per-gamemode copies. Those are not
    /// launchable maps; treating the filename as a stem fills Advanced with
    /// "Thunderdome Small" / "Medium" entries.
    /// </summary>
    static bool PathIsNavmesh(string path)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Equals("navmesh", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? TryMapStemFromFileName(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        // Prefer mp_rr_ (levels). Skip mp_ability_ / mp_weapon_ / mp_common debris.
        var idx = lower.IndexOf("mp_rr_", StringComparison.Ordinal);
        if (idx < 0)
        {
            idx = lower.IndexOf("mp_lobby", StringComparison.Ordinal);
            if (idx < 0)
                return null;
        }

        var rest = fileName[idx..];
        var end = rest.IndexOf('.');
        if (end > 0)
            rest = rest[..end];
        if (rest.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
            rest = rest[..^4];
        return IsMapStem(rest) ? rest : null;
    }

    /// <summary>True only for launchable level stems (not abilities/weapons).</summary>
    public static bool IsMapStem(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var t = token.Trim();
        if (t.StartsWith("mp_ability_", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("mp_weapon_", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("mp_common", StringComparison.OrdinalIgnoreCase))
            return false;
        return s_mapToken.IsMatch(t);
    }

    private sealed class PlaylistEntryBuilder
    {
        public required string Id { get; init; }
        public string? NameKey { get; set; }
        public string? MapNameKey { get; set; }
        public string? Inherit { get; set; }
        public SortedSet<string> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Own-block only -- never resolved through the inherit chain (see r5f_mode trap).
        public bool IsMode { get; set; }
        public bool IsOverlay { get; set; }
        public int ModeOrder { get; set; }
        public string? BlurbRaw { get; set; }
        public string? TitleRaw { get; set; }
        public string? PinnedMapRaw { get; set; }
        public string? MapTitleRaw { get; set; }
        public string? GroupRaw { get; set; }
        public string? FamilyRaw { get; set; }
        public string? FamilyTitleRaw { get; set; }
        public string? IconRaw { get; set; }
        public string? FamilyBlurbRaw { get; set; }
        public int FamilyOrder { get; set; }

        public PlaylistEntry Build(
            IReadOnlyDictionary<string, string> loc,
            IReadOnlyDictionary<string, PlaylistEntryBuilder> all)
        {
            var nameKey = ResolveInheritedField(this, all, b => b.NameKey);
            var mapKey = ResolveInheritedField(this, all, b => b.MapNameKey);

            var mode = TryResolveToken(nameKey, loc);
            var map = TryResolveToken(mapKey, loc);

            string label;
            if (!string.IsNullOrWhiteSpace(mode) && !string.IsNullOrWhiteSpace(map) &&
                !string.Equals(mode, map, StringComparison.OrdinalIgnoreCase))
                label = $"{mode} - {map}";
            else
                label = mode ?? map ?? Id;

            // Mode markers are own-block only; do not walk inherit. The card title
            // overrides the inherited name/map_name pair, which is lobby chrome and
            // resolves to things like "Trios - World's Edge" on a Flowstate mode.
            var blurb = TryResolveToken(BlurbRaw, loc) ?? BlurbRaw?.Trim() ?? string.Empty;
            var titled = TryResolveToken(TitleRaw, loc);
            var title = !string.IsNullOrWhiteSpace(titled)
                ? titled
                : (string.IsNullOrWhiteSpace(TitleRaw) ? label : TitleRaw.Trim());
            var displayLabel = !string.IsNullOrWhiteSpace(title) ? title : label;
            var display = string.Equals(displayLabel, Id, StringComparison.OrdinalIgnoreCase)
                ? Id
                : $"{displayLabel}  ({Id})";
            var pinnedMap = PinnedMapRaw?.Trim() ?? string.Empty;
            return new PlaylistEntry
            {
                Id = Id,
                NameKey = nameKey,
                DisplayName = display,
                Title = title,
                PinnedMap = pinnedMap,
                Maps = Maps.ToList(),
                IsMode = IsMode,
                IsOverlay = IsOverlay,
                ModeOrder = ModeOrder,
                Blurb = blurb,
                MapTitle = TryResolveToken(MapTitleRaw, loc)
                           ?? NormalizeLiteral(MapTitleRaw),
                Group = ModeGroups.Normalize(NormalizeLiteral(GroupRaw)),
                Family = NormalizeLiteral(FamilyRaw),
                FamilyTitle = TryResolveToken(FamilyTitleRaw, loc)
                              ?? NormalizeLiteral(FamilyTitleRaw),
                FamilyOrder = FamilyOrder,
                Icon = NormalizeLiteral(IconRaw),
                FamilyBlurb = TryResolveToken(FamilyBlurbRaw, loc) ?? NormalizeLiteral(FamilyBlurbRaw),
            };
        }
    }

    /// <summary>Walk inherit chain for first usable field value (name / map_name).</summary>
    private static string? ResolveInheritedField(
        PlaylistEntryBuilder start,
        IReadOnlyDictionary<string, PlaylistEntryBuilder> all,
        Func<PlaylistEntryBuilder, string?> getter)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PlaylistEntryBuilder? cur = start;
        while (cur is not null && seen.Add(cur.Id))
        {
            var v = getter(cur);
            if (IsUsableNameKey(v))
                return v;
            if (string.IsNullOrWhiteSpace(cur.Inherit) ||
                !all.TryGetValue(cur.Inherit.Trim(), out cur))
                break;
        }
        return null;
    }

    private static bool IsUsableNameKey(string? nameKey)
    {
        if (string.IsNullOrWhiteSpace(nameKey))
            return false;
        var raw = NormalizeRawToken(nameKey);
        return raw.Length > 0 &&
               !raw.Equals("EMPTY_STRING", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strip surrounding quotes off a plain var value; null becomes empty.</summary>
    private static string NormalizeLiteral(string? raw)
    {
        var v = raw?.Trim() ?? string.Empty;
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            v = v[1..^1].Trim();
        return v;
    }

    private static string NormalizeRawToken(string nameKey)
    {
        var raw = nameKey.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Trim();
        if (raw.StartsWith('#'))
            raw = raw[1..];
        return raw.Trim();
    }

    /// <summary>
    /// Resolve a playlist token to localized text, or null if unknown.
    /// Does not fall back to the playlist id.
    /// </summary>
    private static string? TryResolveToken(string? nameKey, IReadOnlyDictionary<string, string> loc)
    {
        if (!IsUsableNameKey(nameKey))
            return null;

        var raw = nameKey!.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Trim();

        var key = raw.StartsWith('#') ? raw[1..] : raw;
        if (key.Length == 0 || key.Equals("EMPTY_STRING", StringComparison.OrdinalIgnoreCase))
            return null;

        if (loc.TryGetValue(key, out var localized) && !string.IsNullOrWhiteSpace(localized))
            return localized.Trim();

        // Retail dumps store HashName64(token) as lowercase hex (no fixed width).
        var hex = RtechHash.LocKeyFromToken(key);
        if (hex.Length > 0 &&
            loc.TryGetValue(hex, out var byHash) &&
            !string.IsNullOrWhiteSpace(byHash))
            return byHash.Trim();

        if (hex.Length > 0 && hex.Length < 16)
        {
            var hex16 = hex.PadLeft(16, '0');
            if (loc.TryGetValue(hex16, out byHash) && !string.IsNullOrWhiteSpace(byHash))
                return byHash.Trim();
        }

        // Literal name (e.g. BR Dev) — not a token loc key
        if (!raw.StartsWith('#') &&
            !key.StartsWith("PL_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("MP_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("CONTROL_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("TDM_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("GAME_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("FREEDM_", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("mp_", StringComparison.OrdinalIgnoreCase))
            return raw;

        return null;
    }

    /// <summary>
    /// localization_*.txt lines: "KEY" "Value" (tab or spaces between pairs).
    /// </summary>
    private static HashSet<string> CollectLocNeedles(IEnumerable<PlaylistEntryBuilder> entries)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? raw)
        {
            if (!IsUsableNameKey(raw))
                return;
            var key = NormalizeRawToken(raw!);
            if (key.Length == 0)
                return;
            keys.Add(key);
            var hex = RtechHash.LocKeyFromToken(key);
            if (hex.Length == 0)
                return;
            keys.Add(hex);
            if (hex.Length < 16)
                keys.Add(hex.PadLeft(16, '0'));
        }

        foreach (var b in entries)
        {
            Add(b.NameKey);
            Add(b.MapNameKey);
            Add(b.TitleRaw);
            Add(b.BlurbRaw);
            Add(b.FamilyTitleRaw);
            Add(b.MapTitleRaw);
            Add(b.FamilyBlurbRaw);
        }

        return keys;
    }

    public static IReadOnlyDictionary<string, string> LoadLocalization(
        string? path,
        ISet<string>? onlyKeys = null)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path))
            return dict;

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length < 5 || line[0] != '"')
                    continue;

                var kEnd = line.IndexOf('"', 1);
                if (kEnd < 1)
                    continue;
                var key = line[1..kEnd];
                if (key.Length == 0)
                    continue;
                if (onlyKeys is { Count: > 0 } && !onlyKeys.Contains(key))
                    continue;

                var rest = line[(kEnd + 1)..].TrimStart();
                if (rest.Length < 2 || rest[0] != '"')
                    continue;
                var vEnd = rest.LastIndexOf('"');
                if (vEnd <= 1)
                    continue;
                var val = rest[1..vEnd];
                dict[key] = val;
            }
        }
        catch
        {
            // empty dict
        }

        return dict;
    }

    /// <summary>
    /// Root file is `playlists { Gamemodes{} Playlists{ entries... } "PlaylistRotation"... }`.
    /// Only children of the nested **Playlists** section (capital P) are launchable ids.
    /// Root `playlists` must NOT be treated as that section (case-sensitive).
    /// </summary>
    private static void ParsePlaylistsSection(
        string text,
        Dictionary<string, PlaylistEntryBuilder> entries,
        SortedSet<string> allMaps)
    {
        var tokens = Tokenize(StripLineComments(text));
        var depth = 0;
        string? pendingId = null;

        // Nested Playlists { } section only (not root playlists).
        var inPlaylistsSection = false;
        var playlistsDepth = -1;

        string? currentPlaylist = null;
        var playlistDepth = -1;

        var inVars = false;
        var varsDepth = -1;

        var inMaps = false;
        var mapsDepth = -1;

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t == "{")
            {
                depth++;
                if (pendingId is not null)
                {
                    // Case-sensitive: section is "Playlists", root is "playlists".
                    if (pendingId == "Playlists" && !inPlaylistsSection)
                    {
                        inPlaylistsSection = true;
                        playlistsDepth = depth;
                    }
                    else if (inPlaylistsSection &&
                             depth == playlistsDepth + 1 &&
                             !s_skipBlock.Contains(pendingId) &&
                             !s_rejectId.Contains(pendingId) &&
                             !pendingId.StartsWith("PlaylistRotation", StringComparison.OrdinalIgnoreCase) &&
                             !pendingId.StartsWith("PlaylistSchedule", StringComparison.OrdinalIgnoreCase))
                    {
                        currentPlaylist = pendingId;
                        playlistDepth = depth;
                        if (!entries.ContainsKey(pendingId))
                        {
                            entries[pendingId] = new PlaylistEntryBuilder { Id = pendingId };
                        }
                    }
                    else if (string.Equals(pendingId, "vars", StringComparison.OrdinalIgnoreCase) &&
                             currentPlaylist is not null)
                    {
                        inVars = true;
                        varsDepth = depth;
                    }
                    else if (string.Equals(pendingId, "maps", StringComparison.OrdinalIgnoreCase))
                    {
                        inMaps = true;
                        mapsDepth = depth;
                    }

                    pendingId = null;
                }
                continue;
            }

            if (t == "}")
            {
                if (inMaps && depth == mapsDepth)
                {
                    inMaps = false;
                    mapsDepth = -1;
                }

                if (inVars && depth == varsDepth)
                {
                    inVars = false;
                    varsDepth = -1;
                }

                if (currentPlaylist is not null && depth == playlistDepth)
                {
                    currentPlaylist = null;
                    playlistDepth = -1;
                }

                if (inPlaylistsSection && depth == playlistsDepth)
                {
                    inPlaylistsSection = false;
                    playlistsDepth = -1;
                }

                depth = Math.Max(0, depth - 1);
                pendingId = null;
                continue;
            }

            // maps { mp_rr_x 1 } — only level stems, never mp_ability_* / mp_weapon_*.
            if (inMaps && depth >= mapsDepth)
            {
                if (IsMapStem(t))
                {
                    allMaps.Add(t);
                    if (currentPlaylist is not null &&
                        entries.TryGetValue(currentPlaylist, out var b))
                        b.Maps.Add(t);
                }
                pendingId = null;
                continue;
            }

            // playlist body: inherit parent_id
            if (currentPlaylist is not null &&
                depth == playlistDepth &&
                string.Equals(t, "inherit", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count)
            {
                var parent = tokens[i + 1];
                if (parent is not "{" and not "}" &&
                    entries.TryGetValue(currentPlaylist, out var bi) &&
                    string.IsNullOrEmpty(bi.Inherit))
                {
                    bi.Inherit = parent;
                }
                if (parent is not "{" and not "}")
                    i++;
                pendingId = null;
                continue;
            }

            // vars { name #PL_FREE_ROAM } / map_name #MP_... / r5f_mode*
            if (inVars && depth >= varsDepth && currentPlaylist is not null &&
                i + 1 < tokens.Count)
            {
                var val = tokens[i + 1];
                if (val is not "{" and not "}")
                {
                    if (entries.TryGetValue(currentPlaylist, out var b))
                    {
                        if (string.Equals(t, "name", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.NameKey))
                        {
                            b.NameKey = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "map_name", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.MapNameKey))
                        {
                            b.MapNameKey = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        // Own-block only -- never ResolveInheritedField (inheritance trap).
                        if (string.Equals(t, "r5f_mode", StringComparison.OrdinalIgnoreCase))
                        {
                            b.IsMode = val == "1";
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_overlay", StringComparison.OrdinalIgnoreCase))
                        {
                            b.IsOverlay = val == "1";
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_order", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(val, out var order))
                                b.ModeOrder = order;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_blurb", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.BlurbRaw))
                        {
                            b.BlurbRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_title", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.TitleRaw))
                        {
                            b.TitleRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_map", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.PinnedMapRaw))
                        {
                            b.PinnedMapRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_map_title", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.MapTitleRaw))
                        {
                            b.MapTitleRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_group", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.GroupRaw))
                        {
                            b.GroupRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_family", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.FamilyRaw))
                        {
                            b.FamilyRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_family_title", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.FamilyTitleRaw))
                        {
                            b.FamilyTitleRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_family_blurb", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.FamilyBlurbRaw))
                        {
                            b.FamilyBlurbRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_icon", StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(b.IconRaw))
                        {
                            b.IconRaw = val;
                            i++;
                            pendingId = null;
                            continue;
                        }

                        if (string.Equals(t, "r5f_mode_family_order", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(val, out var famOrder))
                                b.FamilyOrder = famOrder;
                            i++;
                            pendingId = null;
                            continue;
                        }
                    }
                }
            }

            // identifier / quoted id followed by {
            if (i + 1 < tokens.Count && tokens[i + 1] == "{" && IsBlockName(t))
            {
                pendingId = t;
                continue;
            }

            // Do not scavenge bare mp_* outside maps{} — picks up abilities/weapons from vars.
            pendingId = null;
        }
    }

    private static bool IsBlockName(string t)
    {
        if (string.IsNullOrEmpty(t) || t is "{" or "}")
            return false;
        // Tokenize already strips quotes; allow PlaylistRotation as block name but we reject as entry.
        if (t[0] is '+' or '-' )
            return false;
        if (char.IsDigit(t[0]))
            return false;
        return true;
    }

    private static string StripLineComments(string text)
    {
        var lines = text.Split('\n');
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var raw in lines)
        {
            var line = raw;
            var cmt = line.IndexOf("//", StringComparison.Ordinal);
            if (cmt >= 0)
                line = line[..cmt];
            sb.Append(line);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '{' or '}')
            {
                list.Add(c.ToString());
                i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                var start = i;
                while (i < text.Length && text[i] != '"')
                    i++;
                list.Add(text[start..i]);
                if (i < text.Length)
                    i++;
                continue;
            }

            var s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}' and not '"')
                i++;
            list.Add(text[s..i]);
        }

        return list;
    }
}
