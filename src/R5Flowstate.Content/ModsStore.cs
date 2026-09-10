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
            if (SafePath.TryJoin(joined, ManifestFileName, out var manPath) && File.Exists(manPath))
            {
                try
                {
                    var man = JsonSerializer.Deserialize<ThunderstoreManifest>(
                        File.ReadAllText(manPath), s_manifestJson);
                    tsVersion = man?.VersionNumber ?? string.Empty;
                }
                catch (JsonException)
                {
                    skipped?.Add($"folder '{folderName}': malformed {ManifestFileName}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    skipped?.Add($"folder '{folderName}': could not read {ManifestFileName}");
                }
            }

            var enabled = !enabledById.TryGetValue(id, out var listed) || listed;
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
                ClientSafe = ModVdf.IsTruthy(doc.Get("client_safe")),
                HasScripts = hasScripts,
                IconPath = iconPath,
                ThunderstoreVersion = tsVersion,
                ThunderstoreFullName = folderName,
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

    sealed class ThunderstoreManifest
    {
        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }
    }
}
