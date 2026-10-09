using System.Text.Json;
using System.Text.Json.Serialization;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Host;

/// <summary>
/// Thunderstore mods for a host install. Every install goes through the same
/// planner and installer as the player launcher (id == Owner.Name, extension
/// allowlist, dependency walk). mods.lock.json pins exact versions; nothing
/// changes a pin except an explicit add or update.
/// </summary>
public sealed class HostMods : IDisposable
{
    public const int MaxLocked = 64;

    readonly string _installPath;
    readonly ThunderstoreClient _ts = new();
    IReadOnlyList<ModPackage>? _catalog;

    public HostMods(string installPath)
    {
        _installPath = installPath;
    }

    public static string LockPath(string installPath) =>
        Path.Combine(HostConfig.HostDir(installPath), "mods.lock.json");

    public async Task<IReadOnlyList<ModPackage>> CatalogAsync(CancellationToken cancel)
    {
        _catalog ??= (await _ts.ListPackagesAsync(cancel).ConfigureAwait(false))
            .Where(p => !p.IsDeprecated && !p.IsNsfw)
            .ToList();
        return _catalog;
    }

    public IReadOnlyList<InstalledMod> Installed() => ModsStore.Discover(_installPath);

    /// <summary>Install pins ("Owner-Name" or "Owner-Name-1.2.3") with their dependencies.</summary>
    public async Task<IReadOnlyList<ModInstallStep>> AddAsync(
        IEnumerable<string> pins, bool required, IProgress<string>? status, CancellationToken cancel)
    {
        var catalog = await CatalogAsync(cancel).ConfigureAwait(false);
        var unresolved = new List<string>();
        var roots = new List<(ModPackage, string?)>();
        foreach (var pin in pins)
        {
            if (TryResolve(catalog, pin, out var pkg, out var ver))
                roots.Add((pkg, ver));
            else
                unresolved.Add(pin);
        }

        var plan = ModInstallPlanner.Plan(catalog, roots, Installed(), unresolved);
        if (unresolved.Count > 0)
            throw new InvalidOperationException("Not on Thunderstore: " + string.Join(", ", unresolved));

        var installer = new ModInstaller(_ts);
        foreach (var step in plan)
        {
            cancel.ThrowIfCancellationRequested();
            status?.Report("installing " + step.Pin);
            var mod = await installer.InstallAsync(_installPath, step.Package, step.Version.VersionNumber, null, cancel)
                .ConfigureAwait(false);
            ModsStore.AddOrEnable(_installPath, mod.Id);
        }

        var lockFile = ReadLock();
        foreach (var step in plan)
            lockFile.Pins[step.Package.FullName] = step.Version.VersionNumber;
        if (lockFile.Pins.Count > MaxLocked)
            throw new InvalidOperationException("More than " + MaxLocked + " mods locked.");
        WriteLock(lockFile);

        SetPolicy(roots.Select(r => ModInstaller.ExpectedCatalogId(r.Item1)), required);
        return plan;
    }

    /// <summary>Newest version of each locked mod; returns the pins that moved.</summary>
    public async Task<IReadOnlyList<string>> UpdateAllAsync(IProgress<string>? status, CancellationToken cancel)
    {
        var catalog = await CatalogAsync(cancel).ConfigureAwait(false);
        var lockFile = ReadLock();
        var pins = new List<string>();
        foreach (var (full, ver) in lockFile.Pins)
        {
            var pkg = catalog.FirstOrDefault(p => string.Equals(p.FullName, full, StringComparison.OrdinalIgnoreCase));
            var latest = pkg?.Versions.FirstOrDefault()?.VersionNumber;
            if (pkg is not null && latest is not null && latest != ver)
                pins.Add(full + "-" + latest);
        }
        if (pins.Count > 0)
            await AddAsync(pins, required: true, status, cancel).ConfigureAwait(false);
        return pins;
    }

    /// <summary>Reinstalls exactly the locked versions (fresh box, or after a repair).</summary>
    public async Task SyncAsync(IProgress<string>? status, CancellationToken cancel)
    {
        var lockFile = ReadLock();
        if (lockFile.Pins.Count == 0)
            return;
        var have = Installed().ToDictionary(m => m.ThunderstoreFullName, m => m.ThunderstoreVersion, StringComparer.OrdinalIgnoreCase);
        var missing = lockFile.Pins
            .Where(kv => !have.TryGetValue(kv.Key, out var v) || v != kv.Value)
            .Select(kv => kv.Key + "-" + kv.Value)
            .ToList();
        if (missing.Count > 0)
            await AddAsync(missing, required: true, status, cancel).ConfigureAwait(false);
    }

    public void Remove(string fullName)
    {
        var mod = Installed().FirstOrDefault(m =>
            string.Equals(m.ThunderstoreFullName, fullName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.FolderName, fullName, StringComparison.OrdinalIgnoreCase));
        if (mod is null)
            throw new InvalidOperationException("Not installed: " + fullName);
        ModsStore.Uninstall(_installPath, mod.FolderName);

        var lockFile = ReadLock();
        lockFile.Pins.Remove(mod.ThunderstoreFullName);
        WriteLock(lockFile);
        SetPolicy(new[] { mod.Id }, required: false, remove: true);
    }

    public async Task<IReadOnlyList<ModInstallStep>> ImportProfileAsync(
        string code, IProgress<string>? status, CancellationToken cancel)
    {
        var profile = await _ts.GetProfileAsync(code.Trim(), cancel).ConfigureAwait(false);
        return await AddAsync(profile.Packages, required: true, status, cancel).ConfigureAwait(false);
    }

    void SetPolicy(IEnumerable<string> ids, bool required, bool remove = false)
    {
        var req = ModsStore.ReadRequiredMods(_installPath).ToList();
        var allowed = ModsStore.ReadAllowedMods(_installPath).ToList();
        foreach (var id in ids)
        {
            if (!ModId.IsValid(id))
                continue;
            req.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            allowed.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (remove)
                continue;
            if (required)
                req.Add((id, true));
            allowed.Add((id, true));
        }
        ModsStore.WriteRequiredMods(_installPath, req.Take(MaxLocked));
        ModsStore.WriteAllowedMods(_installPath, allowed.Take(MaxLocked));
    }

    static bool TryResolve(IReadOnlyList<ModPackage> catalog, string pin, out ModPackage package, out string? version)
    {
        package = null!;
        version = null;
        if (!ModInstallPlanner.TrySplitPin(pin, out var full, out var ver))
            full = pin.Trim();
        foreach (var pkg in catalog)
        {
            if (string.Equals(pkg.FullName, full, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pkg.FullName, pin.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                package = pkg;
                version = string.Equals(pkg.FullName, pin.Trim(), StringComparison.OrdinalIgnoreCase) ? null : ver;
                return true;
            }
        }
        return false;
    }

    public ModLock ReadLock()
    {
        var path = LockPath(_installPath);
        if (!File.Exists(path))
            return new ModLock();
        return JsonSerializer.Deserialize<ModLock>(File.ReadAllText(path)) ?? new ModLock();
    }

    void WriteLock(ModLock lockFile)
    {
        var path = LockPath(_installPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(lockFile, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() => _ts.Dispose();
}

public sealed class ModLock
{
    [JsonPropertyName("pins")]
    public SortedDictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
