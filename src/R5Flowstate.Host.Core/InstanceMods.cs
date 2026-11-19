using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Host;

public sealed class InstanceMod
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Players must have it to join; otherwise it is allowed but optional.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
}

/// <summary>What one server runs: its picks, plus the mod that owns its playlist or map and their dependencies.</summary>
public sealed record InstanceModPlan(
    IReadOnlyList<InstalledMod> Installed,
    IReadOnlySet<string> Enabled,
    IReadOnlySet<string> Required,
    IReadOnlyDictionary<string, string> Forced,
    IReadOnlyList<string> Revoked);

/// <summary>
/// The engine reads mods/mods.vdf, required_mods.vdf and allowed_mods.vdf once at boot,
/// so a per-server mod set is those three files written just before that server starts.
/// Starts are serialized by <see cref="DediSupervisor"/> so two servers never read each other's set.
/// </summary>
public static class InstanceMods
{
    static readonly Regex Token = new("\"[^\"]*\"|[{}]|[^\\s{}\"]+", RegexOptions.Compiled);

    public static InstanceModPlan Plan(string installPath, HostInstance instance)
    {
        var installed = ModsStore.Discover(installPath);
        var usable = ModRevocation.Usable(installed, CuratedCatalog.LoadLastKnown(installPath));
        var revoked = installed.Except(usable).Select(m => m.Id).ToList();
        var byId = usable.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var forced = ForcedMods(installPath, usable, instance.Playlist, instance.Map);

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pick in instance.Mods)
        {
            if (!byId.ContainsKey(pick.Id))
                continue;
            enabled.Add(pick.Id);
            if (pick.Required)
                required.Add(pick.Id);
        }
        foreach (var id in forced.Keys)
        {
            enabled.Add(id);
            required.Add(id);
        }

        var queue = new Queue<string>(enabled);
        while (queue.Count > 0)
        {
            if (!byId.TryGetValue(queue.Dequeue(), out var mod))
                continue;
            var needsAll = required.Contains(mod.Id);
            foreach (var dep in mod.Dependencies)
            {
                var target = usable.FirstOrDefault(m => !string.IsNullOrEmpty(m.ThunderstoreFullName) &&
                    dep.StartsWith(m.ThunderstoreFullName + "-", StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    continue;
                if (needsAll)
                    required.Add(target.Id);
                if (enabled.Add(target.Id))
                    queue.Enqueue(target.Id);
            }
        }
        return new InstanceModPlan(installed, enabled, required, forced, revoked);
    }

    /// <summary>Mod id -> reason, for the mod that declares the instance's playlist or map.</summary>
    public static IReadOnlyDictionary<string, string> ForcedMods(
        string installPath, IReadOnlyList<InstalledMod> installed, string? playlist, string? map)
    {
        var forced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed)
        {
            if (!string.IsNullOrEmpty(map) && mod.Maps.Contains(map, StringComparer.OrdinalIgnoreCase))
                forced[mod.Id] = "map " + map;
            else if (!string.IsNullOrEmpty(playlist) && PlaylistIds(installPath, mod).Contains(playlist))
                forced[mod.Id] = "playlist " + playlist;
        }
        return forced;
    }

    static HashSet<string> PlaylistIds(string installPath, InstalledMod mod)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!SafePath.TryJoin(ModsStore.ModsDirectory(installPath), mod.FolderName, out var folder) ||
            !SafePath.TryJoin(folder, "playlists_r5_patch.txt", out var patch) || !File.Exists(patch))
            return ids;
        var info = new FileInfo(patch);
        if (info.Length > 4 * 1024 * 1024)
            return ids;
        var text = Regex.Replace(File.ReadAllText(patch), "//[^\n]*", "");
        var tokens = Token.Matches(text).Select(m => m.Value).ToList();
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok == "{")
                depth++;
            else if (tok == "}")
                depth--;
            else if (depth == 2 && i + 1 < tokens.Count && tokens[i + 1] == "{")
                ids.Add(tok.Trim('"'));
        }
        return ids;
    }

    /// <summary>Writes the three engine lists for this server. Every installed mod is listed, because the engine enables unlisted ones.</summary>
    public static InstanceModPlan Apply(string installPath, HostInstance instance)
    {
        var plan = Plan(installPath, instance);
        ModsStore.SetModList(installPath, plan.Installed
            .Select(m => (m.Id, plan.Enabled.Contains(m.Id)))
            .ToList());
        ModsStore.WriteRequiredMods(installPath, plan.Required.Select(id => (id, true)));
        ModsStore.WriteAllowedMods(installPath, plan.Enabled.Select(id => (id, true)));
        return plan;
    }
}
