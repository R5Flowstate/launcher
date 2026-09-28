namespace R5Flowstate.Linux.Core;

/// <summary>One Proton build found under Steam (stock or custom like GE-Proton).</summary>
public sealed record ProtonEntry(
    string Name,
    string Dir,
    string ProtonScript,
    string Version,
    bool IsExperimental);

/// <summary>Scans Steam library dirs for Proton builds on CachyOS/Arch.</summary>
public static class ProtonDiscovery
{
    public static IReadOnlyList<ProtonEntry> Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, ".local/share/Steam/steamapps/common"),
            Path.Combine(home, ".local/share/Steam/compatibilitytools.d"),
            Path.Combine(home, ".steam/steam/steamapps/common"),
            Path.Combine(home, ".steam/steam/compatibilitytools.d"),
        };

        var found = new List<ProtonEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { continue; }

            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (!name.Contains("Proton", StringComparison.OrdinalIgnoreCase))
                    continue;
                var script = Path.Combine(d, "proton");
                if (!File.Exists(script))
                    continue;
                var full = Path.GetFullPath(d);
                if (!seen.Add(full))
                    continue;

                string version = "?";
                try
                {
                    var vf = Path.Combine(d, "version");
                    if (File.Exists(vf))
                        version = File.ReadAllText(vf).Trim();
                }
                catch { }

                found.Add(new ProtonEntry(
                    Name: name,
                    Dir: full,
                    ProtonScript: script,
                    Version: version,
                    IsExperimental: name.Contains("Experimental", StringComparison.OrdinalIgnoreCase)));
            }
        }

        // Prefer Experimental/latest first, then sort by name.
        return found
            .OrderByDescending(p => p.IsExperimental)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ProtonEntry? PickLatest(IReadOnlyList<ProtonEntry> entries)
        => entries.FirstOrDefault();
}
