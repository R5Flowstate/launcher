using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>Owns <c>&lt;install&gt;/mods</c>. Callers pass the install path; this never reads the registry.</summary>
public static class ModsStore
{
    public const string ModListFileName = "mods.vdf";
    public const string RequiredModsFileName = "required_mods.vdf";
    public const string AllowedModsFileName = "allowed_mods.vdf";
    public const string ModSettingsFileName = "mod.vdf";

    // Engine MOD_MAX_MANIFEST_BYTES.
    public const long MaxModSettingsBytes = 4 * 1024 * 1024;

    /// <summary>mod.vdf text, or null when it is larger than the game accepts.</summary>
    public static string? ReadModSettingsText(string vdfPath) =>
        new FileInfo(vdfPath).Length > MaxModSettingsBytes ? null : File.ReadAllText(vdfPath);
    public const string ManifestFileName = "manifest.json";
    public const string IconFileName = "icon.png";
    public const string ScriptsRsonRelative = "scripts/vscripts/scripts.rson";

    static readonly JsonSerializerOptions s_manifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ModsDirectory(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));
        if (!SafePath.TryJoin(installPath, ProductConstants.ModsDirName, out var mods))
            throw new InvalidOperationException("Refusing mods path outside install root.");
        return mods;
    }

    public static IReadOnlyList<InstalledMod> Discover(string installPath) =>
        Discover(installPath, skipped: null);

    public static IReadOnlyList<InstalledMod> Discover(string installPath, ICollection<string>? skipped)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return Array.Empty<InstalledMod>();

        string modsDir;
        try
        {
            modsDir = ModsDirectory(installPath);
        }
        catch
        {
            return Array.Empty<InstalledMod>();
        }

        if (!Directory.Exists(modsDir))
            return Array.Empty<InstalledMod>();

        var order = LoadModList(modsDir);
        var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var enabledById = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            if (!orderIndex.ContainsKey(order[i].Id))
                orderIndex[order[i].Id] = i;
            enabledById[order[i].Id] = order[i].Enabled;
        }

        var found = new List<InstalledMod>();
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(modsDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<InstalledMod>();
        }

        foreach (var dir in dirs)
        {
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
            }
            catch
            {
                continue;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var folderName = info.Name;
            if (folderName.StartsWith('.'))
                continue;

            if (!SafePath.TryJoin(modsDir, folderName, out var joined) ||
                !string.Equals(joined, info.FullName, StringComparison.OrdinalIgnoreCase))
            {
                skipped?.Add($"folder '{folderName}': path is not a direct child of mods/");
                continue;
            }

            if (!SafePath.TryJoin(joined, ModSettingsFileName, out var vdfPath) || !File.Exists(vdfPath))
                continue;

            string text;
            try
            {
                if (new FileInfo(vdfPath).Length > MaxModSettingsBytes)
                {
                    skipped?.Add($"folder '{folderName}': {ModSettingsFileName} is larger than the game accepts");
                    continue;
                }

                text = File.ReadAllText(vdfPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped?.Add($"folder '{folderName}': could not read {ModSettingsFileName}");
                continue;
            }

            var doc = ModVdf.Parse(text);
            var id = doc.Get("id");
            if (string.IsNullOrEmpty(id))
            {
                skipped?.Add($"folder '{folderName}': missing mod id");
                continue;
            }

            if (!ModId.IsValid(id))
            {
                skipped?.Add($"folder '{folderName}': invalid mod id '{id}'");
                continue;
            }

            string? iconPath = null;
            if (SafePath.TryJoin(joined, IconFileName, out var icon) && File.Exists(icon))
                iconPath = icon;

            var hasScripts = SafePath.TryJoin(joined, ScriptsRsonRelative, out var rson) && File.Exists(rson);

            var tsVersion = string.Empty;
            var tsFullName = string.Empty;
            IReadOnlyList<string> deps = Array.Empty<string>();
            if (SafePath.TryJoin(joined, ManifestFileName, out var manPath) && File.Exists(manPath))
            {
                // Same size cap as the catalog install path; this runs on every mods refresh.
                var man = ReadManifest(manPath);
                if (man is null)
                    skipped?.Add($"folder '{folderName}': {ManifestFileName} is unreadable, malformed or over {MaxManifestBytes / 1024} KiB");
                else
                {
                    tsVersion = man.VersionNumber ?? string.Empty;
                    tsFullName = folderName;
                    deps = (man.Dependencies ?? new List<string>())
                        .Where(d => !string.IsNullOrWhiteSpace(d) && d.Length <= 256)
                        .Take(64)
                        .ToList();
                }
            }

            var enabled = !enabledById.TryGetValue(id, out var listed) || listed;
            var nameSpace = ModOwnership.Namespace(id);
            var maps = ModOwnership.ReadMaps(doc, nameSpace, out _) ?? Array.Empty<string>();
            var replaces = new List<string>();
            foreach (var table in ModOwnership.ReadDatatableOverrides(doc, out _) ?? Array.Empty<string>())
                replaces.Add("datatable " + table);
            foreach (var token in ModOwnership.ReadLocalizationOverrides(doc, out _) ?? Array.Empty<string>())
            {
                if (!ModOwnership.OwnsLocKey(nameSpace, maps, token))
                    replaces.Add("text " + token);
            }
            found.Add(new InstalledMod
            {
                FolderName = folderName,
                Id = id,
                Name = doc.Get("name") ?? string.Empty,
                Author = doc.Get("author") ?? string.Empty,
                Version = doc.Get("version") ?? string.Empty,
                Description = doc.Get("description") ?? string.Empty,
                Enabled = enabled,
                Order = 0,
                Realm = doc.Get("realm") ?? string.Empty,
                HasScripts = hasScripts,
                IconPath = iconPath,
                ThunderstoreVersion = tsVersion,
                ThunderstoreFullName = tsFullName,
                Dependencies = deps,
                Replaces = replaces,
                Maps = maps,
            });
        }

        found.Sort((a, b) =>
        {
            var aKnown = orderIndex.TryGetValue(a.Id, out var ai);
            var bKnown = orderIndex.TryGetValue(b.Id, out var bi);
            if (aKnown && bKnown)
                return ai.CompareTo(bi);
            if (aKnown)
                return -1;
            if (bKnown)
                return 1;
            return string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        for (var i = 0; i < found.Count; i++)
            found[i].Order = i;

        return found;
    }

    public const string StagingPrefix = ".__mod_";
    public const string BackupPrefix = ".__old_";

    /// <summary>
    /// Deletes install leftovers (staging zips and folders, replaced copies) that a crash or a
    /// killed launcher left in mods/. Only entries older than <paramref name="minAge"/> go, so an
    /// install running in another process keeps its staging.
    /// </summary>
    public static int SweepStaging(string installPath, TimeSpan minAge)
    {
        string modsDir;
        try
        {
            modsDir = ModsDirectory(installPath);
        }
        catch
        {
            return 0;
        }

        if (!Directory.Exists(modsDir))
            return 0;

        var cutoff = DateTime.UtcNow - minAge;
        var removed = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(modsDir).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal) &&
                !name.StartsWith(BackupPrefix, StringComparison.Ordinal))
                continue;
            if (!SafePath.TryJoin(modsDir, name, out var full) ||
                !string.Equals(full, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (IsReparsePoint(full))
                    continue;
                if (Directory.Exists(full))
                {
                    if (Directory.GetLastWriteTimeUtc(full) > cutoff)
                        continue;
                    DeleteDirectoryNoReparse(full);
                }
                else if (File.Exists(full))
                {
                    if (File.GetLastWriteTimeUtc(full) > cutoff)
                        continue;
                    File.SetAttributes(full, FileAttributes.Normal);
                    File.Delete(full);
                }
                else
                {
                    continue;
                }

                removed++;
            }
            catch
            {
                // Still in use; the next sweep gets it.
            }
        }

        return removed;
    }

    /// <summary>Bytes under one mod folder; stops counting past <paramref name="maxFiles"/> files.</summary>
    public static long FolderSizeBytes(string installPath, string folderName, int maxFiles = 20000)
    {
        try
        {
            var modsDir = ModsDirectory(installPath);
            if (!SafePath.TryJoin(modsDir, folderName, out var dir) || !Directory.Exists(dir) || IsReparsePoint(dir))
                return 0;
            long total = 0;
            var count = 0;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                total += file.Length;
                if (++count >= maxFiles)
                    break;
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    public static void SetEnabled(string installPath, string id, bool enabled)
    {
        if (!ModId.IsValid(id))
            throw new ArgumentException("Invalid mod id.", nameof(id));

        MutateModList(installPath, current =>
        {
            var next = new List<(string Id, bool Enabled)>(current.Count + 1);
            var seen = false;
            foreach (var row in current)
            {
                if (string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    next.Add((row.Id, enabled));
                    seen = true;
                }
                else
                {
                    next.Add(row);
                }
            }

            if (!seen)
                next.Add((id, enabled));
            return next;
        });
    }

    /// <summary>Puts <paramref name="rows"/> first, in order, with their enabled state; other rows keep theirs. One write.</summary>
    public static void SetModList(string installPath, IReadOnlyList<(string Id, bool Enabled)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        MutateModList(installPath, current =>
        {
            var next = new List<(string Id, bool Enabled)>(rows.Count + current.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (ModId.IsValid(row.Id) && seen.Add(row.Id))
                    next.Add(row);
            }

            foreach (var row in current)
            {
                if (seen.Add(row.Id))
                    next.Add(row);
            }

            return next;
        });
    }

    public static void Reorder(string installPath, IReadOnlyList<string> idsInOrder)
    {
        ArgumentNullException.ThrowIfNull(idsInOrder);

        MutateModList(installPath, current =>
        {
            var byId = new Dictionary<string, (string Id, bool Enabled)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in current)
            {
                if (!byId.ContainsKey(row.Id))
                    byId[row.Id] = row;
            }

            var next = new List<(string Id, bool Enabled)>(current.Count + idsInOrder.Count);
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in idsInOrder)
            {
                if (string.IsNullOrEmpty(id) || !placed.Add(id))
                    continue;
                if (byId.TryGetValue(id, out var existing))
                    next.Add((existing.Id, existing.Enabled));
                else if (ModId.IsValid(id))
                    next.Add((id, true));
            }

            foreach (var row in current)
            {
                if (placed.Add(row.Id))
                    next.Add(row);
            }

            return next;
        });
    }

    public static IReadOnlyList<string> EnabledIds(string installPath)
    {
        var modsDir = ModsDirectory(installPath);
        var list = LoadModList(modsDir);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in list)
        {
            if (!row.Enabled || !seen.Add(row.Id))
                continue;
            ids.Add(row.Id);
        }

        return ids;
    }

    public static void Uninstall(string installPath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name is required.", nameof(folderName));
        if (folderName.StartsWith('.'))
            throw new InvalidOperationException("Refusing to uninstall a staging folder.");

        var modsDir = ModsDirectory(installPath);
        if (!SafePath.TryJoin(modsDir, folderName, out var target))
            throw new InvalidOperationException("Refusing path outside mods/: " + folderName);

        var modsFull = Path.GetFullPath(modsDir);
        var parent = Path.GetDirectoryName(target);
        if (!string.Equals(parent, modsFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mod folder must be a direct child of mods/.");

        if (!Directory.Exists(target))
            throw new InvalidOperationException("Mod folder does not exist: " + folderName);
        if (IsReparsePoint(target))
            throw new InvalidOperationException("Refusing to delete a reparse point.");
        if (!SafePath.TryJoin(target, ModSettingsFileName, out var vdfPath) || !File.Exists(vdfPath))
            throw new InvalidOperationException("Refusing to delete a folder without " + ModSettingsFileName);

        var id = ModVdf.Parse(File.ReadAllText(vdfPath)).Get("id");
        DeleteDirectoryNoReparse(target);

        if (!string.IsNullOrEmpty(id))
        {
            MutateModList(installPath, current =>
            {
                var next = new List<(string Id, bool Enabled)>(current.Count);
                foreach (var row in current)
                {
                    if (!string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase))
                        next.Add(row);
                }

                return next;
            });
        }
    }

    public static IReadOnlyList<(string Id, bool Enabled)> ReadRequiredMods(string installPath) =>
        ReadNamedList(installPath, RequiredModsFileName);

    public static void WriteRequiredMods(string installPath, IEnumerable<(string Id, bool Enabled)> entries) =>
        WriteNamedList(installPath, RequiredModsFileName, "RequiredMods", entries);

    public static IReadOnlyList<(string Id, bool Enabled)> ReadAllowedMods(string installPath) =>
        ReadNamedList(installPath, AllowedModsFileName);

    public static void WriteAllowedMods(string installPath, IEnumerable<(string Id, bool Enabled)> entries) =>
        WriteNamedList(installPath, AllowedModsFileName, "AllowedMods", entries);

    public static void AddOrEnable(string installPath, string id)
    {
        if (!ModId.IsValid(id))
            throw new ArgumentException("Invalid mod id.", nameof(id));

        MutateModList(installPath, current =>
        {
            var next = new List<(string Id, bool Enabled)>(current.Count + 1);
            var seen = false;
            foreach (var row in current)
            {
                if (string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    next.Add((row.Id, true));
                    seen = true;
                }
                else
                {
                    next.Add(row);
                }
            }

            if (!seen)
                next.Add((id, true));
            return next;
        });
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void DeleteDirectoryNoReparse(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        if (IsReparsePoint(dir))
            throw new InvalidOperationException("Refusing to delete a reparse point.");

        string[] files;
        string[] subs;
        try
        {
            files = Directory.GetFiles(dir);
            subs = Directory.GetDirectories(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not enumerate " + dir, e);
        }

        foreach (var file in files)
        {
            try
            {
                if (IsReparsePoint(file))
                {
                    File.Delete(file);
                    continue;
                }

                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Could not delete " + file, e);
            }
        }

        foreach (var sub in subs)
        {
            if (IsReparsePoint(sub))
            {
                Directory.Delete(sub, recursive: false);
                continue;
            }

            DeleteDirectoryNoReparse(sub);
        }

        Directory.Delete(dir, recursive: false);
    }

    static IReadOnlyList<(string Id, bool Enabled)> ReadNamedList(string installPath, string fileName)
    {
        var modsDir = ModsDirectory(installPath);
        if (!SafePath.TryJoin(modsDir, fileName, out var path) || !File.Exists(path))
            return Array.Empty<(string, bool)>();
        try
        {
            return Collapse(ModVdf.ReadModList(File.ReadAllText(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, bool)>();
        }
    }

    static void WriteNamedList(
        string installPath,
        string fileName,
        string rootKey,
        IEnumerable<(string Id, bool Enabled)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var modsDir = ModsDirectory(installPath);
        Directory.CreateDirectory(modsDir);
        if (!SafePath.TryJoin(modsDir, fileName, out var path))
            throw new InvalidOperationException("Refusing path outside mods/: " + fileName);

        var pairs = Collapse(entries.ToList()).Select(e =>
            new KeyValuePair<string, string>(e.Id, e.Enabled ? "1" : "0"));
        WriteAtomic(path, ModVdf.Write(rootKey, pairs));
    }

    static void MutateModList(
        string installPath,
        Func<List<(string Id, bool Enabled)>, List<(string Id, bool Enabled)>> mutate)
    {
        var modsDir = ModsDirectory(installPath);
        Directory.CreateDirectory(modsDir);
        if (!SafePath.TryJoin(modsDir, ModListFileName, out var path))
            throw new InvalidOperationException("Refusing mods.vdf path outside mods/.");

        var current = LoadModList(modsDir);
        var next = mutate(current);
        WriteAtomic(path, ModVdf.WriteModList(next));
    }

    static List<(string Id, bool Enabled)> LoadModList(string modsDir)
    {
        if (!SafePath.TryJoin(modsDir, ModListFileName, out var path) || !File.Exists(path))
            return new List<(string, bool)>();
        try
        {
            return Collapse(ModVdf.ReadModList(File.ReadAllText(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new List<(string, bool)>();
        }
    }

    static List<(string Id, bool Enabled)> Collapse(List<(string Id, bool Enabled)> rows)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string Id, bool Enabled)>();
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Id))
                continue;
            if (seen.TryGetValue(row.Id, out var idx))
                result[idx] = (result[idx].Id, row.Enabled);
            else
            {
                seen[row.Id] = result.Count;
                result.Add(row);
            }
        }

        return result;
    }

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(text);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public const int MaxManifestBytes = 64 * 1024;

    /// <summary>Thunderstore manifest.json, or null when missing, oversized or malformed.</summary>
    public static ThunderstoreManifest? ReadManifest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxManifestBytes)
                return null;
            return JsonSerializer.Deserialize<ThunderstoreManifest>(File.ReadAllText(path), s_manifestJson);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public sealed class ThunderstoreManifest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }

        [JsonPropertyName("dependencies")]
        public List<string>? Dependencies { get; set; }
    }
}
