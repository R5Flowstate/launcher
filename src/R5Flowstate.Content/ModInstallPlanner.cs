using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public sealed record ModInstallStep(ModPackage Package, ModPackageVersion Version)
{
    public string Pin => Package.Owner + "-" + Package.Name + "-" + Version.VersionNumber;
}

/// <summary>
/// Expands requested catalog packages with their Thunderstore dependencies,
/// dependencies first. Dependency strings come from the listing, so the walk is
/// bounded and every pin must resolve against the same catalog.
/// </summary>
public static class ModInstallPlanner
{
    public const int MaxPackages = 64;

    public static IReadOnlyList<ModInstallStep> Plan(
        IReadOnlyList<ModPackage> catalog,
        IEnumerable<(ModPackage Package, string? Version)> roots,
        IReadOnlyList<InstalledMod> installed,
        List<string> unresolved,
        bool keepInstalledRoots = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(unresolved);

        var byFull = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in catalog)
        {
            if (!string.IsNullOrEmpty(pkg.FullName))
                byFull.TryAdd(pkg.FullName, pkg);
        }

        var installedVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed ?? Array.Empty<InstalledMod>())
        {
            if (!string.IsNullOrEmpty(mod.FolderName))
                installedVersion[mod.FolderName] = mod.ThunderstoreVersion;
        }

        var steps = new List<ModInstallStep>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ModPackage pkg, string? wanted, bool isRoot)
        {
            var full = pkg.FullName;
            if (done.Contains(full) || onStack.Contains(full))
                return;
            if (done.Count + onStack.Count >= MaxPackages)
                throw new InvalidOperationException(
                    "Mod install plan exceeds " + MaxPackages + " packages; refusing.");

            var version = PickVersion(pkg, wanted);
            if (version is null)
            {
                unresolved.Add(full + (string.IsNullOrEmpty(wanted) ? string.Empty : "-" + wanted));
                return;
            }

            if ((!isRoot || keepInstalledRoots) &&
                installedVersion.TryGetValue(full, out var have) &&
                CompareVersions(have, version.VersionNumber) >= 0)
            {
                done.Add(full);
                return;
            }

            onStack.Add(full);
            foreach (var dep in version.Dependencies ?? Array.Empty<string>())
            {
                if (!TrySplitPin(dep, out var depFull, out var depVersion) ||
                    !byFull.TryGetValue(depFull, out var depPkg))
                {
                    unresolved.Add(dep);
                    continue;
                }

                Visit(depPkg, depVersion, isRoot: false);
            }

            onStack.Remove(full);
            done.Add(full);
            steps.Add(new ModInstallStep(pkg, version));
        }

        foreach (var (pkg, wanted) in roots)
        {
            if (pkg is null || string.IsNullOrEmpty(pkg.FullName))
                continue;
            Visit(pkg, wanted, isRoot: true);
        }

        return steps;
    }

    /// <summary>Splits <c>Owner-Name-1.2.3</c> into <c>Owner-Name</c> and <c>1.2.3</c>.</summary>
    public static bool TrySplitPin(string? pin, out string fullName, out string? version)
    {
        fullName = string.Empty;
        version = null;
        if (string.IsNullOrWhiteSpace(pin))
            return false;

        var raw = pin.Trim();
        var last = raw.LastIndexOf('-');
        if (last > 0 && last < raw.Length - 1 && char.IsDigit(raw[last + 1]))
        {
            fullName = raw[..last];
            version = raw[(last + 1)..];
        }
        else
        {
            fullName = raw;
        }

        return fullName.IndexOf('-') > 0;
    }

    static ModPackageVersion? PickVersion(ModPackage pkg, string? wanted)
    {
        var versions = pkg.Versions ?? Array.Empty<ModPackageVersion>();
        if (versions.Count == 0)
            return null;
        if (string.IsNullOrWhiteSpace(wanted))
            return versions[0];
        foreach (var v in versions)
        {
            if (string.Equals(v.VersionNumber, wanted, StringComparison.OrdinalIgnoreCase))
                return v;
        }

        return null;
    }

    static int CompareVersions(string? a, string? b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return va.CompareTo(vb);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }
}
