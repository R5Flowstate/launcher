using System.Text.Json;
using System.Text.Json.Serialization;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Host;

/// <summary>
/// Thunderstore mods for a host install, limited to the R5Flowstate-reviewed list: the
/// same planner and installer as the player launcher (id == Owner.Name, extension
/// allowlist, dependency walk), and every download must match its reviewed sha256.
/// mods.lock.json pins exact versions; nothing changes a pin except an explicit add or update.
/// </summary>
public sealed class HostMods : IDisposable
{
    public const int MaxLocked = 64;

    readonly string _installPath;
    readonly ThunderstoreClient _ts = new();
    IReadOnlyList<ModPackage>? _catalog;
    CuratedList? _curated;

    public HostMods(string installPath)
    {
        _installPath = installPath;
    }

    public static string LockPath(string installPath) =>
        Path.Combine(HostConfig.HostDir(installPath), "mods.lock.json");

    public Task<IReadOnlyList<ModPackage>> CatalogAsync(CancellationToken cancel) => CatalogAsync(refresh: false, cancel);

    /// <summary>Installs pass <paramref name="refresh"/> so a revoke reaches a host tool that stayed open.</summary>
    async Task<IReadOnlyList<ModPackage>> CatalogAsync(bool refresh, CancellationToken cancel)
    {
        if (_catalog is not null && !refresh)
            return _catalog;
        _catalog = null;
        _curated = null;
        _curated = await CuratedCatalog.LoadAsync(_installPath, refresh, cancel, gameCopyOptional: true).ConfigureAwait(false);
        IReadOnlyList<ModPackage> listing = Array.Empty<ModPackage>();
        try
        {
            listing = await _ts.ListPackagesAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // Display fields only; what may be installed comes from the curated list.
        }

        _catalog = CuratedCatalog.ToPackages(_curated, listing);
        return _catalog;
    }

    public IReadOnlyList<InstalledMod> Installed() => ModsStore.Discover(_installPath);

    /// <summary>Install pins ("Owner-Name" or "Owner-Name-1.2.3") with their dependencies.</summary>
    public async Task<IReadOnlyList<ModInstallStep>> AddAsync(
        IEnumerable<string> pins, bool required, IProgress<string>? status, CancellationToken cancel,
        IProgress<ContentInstallProgress>? progress = null)
    {
        var catalog = await CatalogAsync(refresh: true, cancel).ConfigureAwait(false);
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
            throw new InvalidOperationException("Not on the R5Flowstate reviewed list: " + string.Join(", ", unresolved));

        var installer = new ModInstaller(_ts);
        for (var i = 0; i < plan.Count; i++)
        {
            var step = plan[i];
            cancel.ThrowIfCancellationRequested();
            status?.Report("installing " + step.Pin);
            var stepProgress = progress is null ? null : new StepProgress(progress, step.Pin, i, plan.Count);
            var pinned = _curated?.FindVersion(step.Package.FullName, step.Version.VersionNumber)
                         ?? throw new InvalidOperationException("Not on the R5Flowstate reviewed list: " + step.Pin);
            var mod = await installer.InstallAsync(_installPath, step.Package, step.Version.VersionNumber, stepProgress, cancel,
                    pinned.Sha256, pinned.Size)
                .ConfigureAwait(false);
            if (SafePath.TryJoin(ModsStore.ModsDirectory(_installPath), mod.FolderName, out var modDir))
                VerifiedInstalls.Record(_installPath, mod.FolderName, step.Package.FullName + "-" + pinned.Version, pinned.Sha256, modDir);
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
    public async Task<IReadOnlyList<string>> UpdateAllAsync(
        IProgress<string>? status, CancellationToken cancel, IProgress<ContentInstallProgress>? progress = null)
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
            await AddAsync(pins, required: true, status, cancel, progress).ConfigureAwait(false);
        return pins;
    }

    /// <summary>Reinstalls exactly the locked versions (fresh box, or after a repair).</summary>
    public async Task SyncAsync(
        IProgress<string>? status, CancellationToken cancel, IProgress<ContentInstallProgress>? progress = null)
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
            await AddAsync(missing, required: true, status, cancel, progress).ConfigureAwait(false);
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
        string code, IProgress<string>? status, CancellationToken cancel,
        IProgress<ContentInstallProgress>? progress = null)
    {
        var profile = await _ts.GetProfileAsync(code.Trim(), cancel).ConfigureAwait(false);
        return await AddAsync(profile.Packages, required: true, status, cancel, progress).ConfigureAwait(false);
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

    sealed class StepProgress(IProgress<ContentInstallProgress> inner, string pin, int index, int count)
        : IProgress<ContentInstallProgress>
    {
        public void Report(ContentInstallProgress value)
        {
            value.Track = pin;
            value.StepIndex = index;
            value.StepCount = count;
            inner.Report(value);
        }
    }
}

public sealed class ModLock
{
    [JsonPropertyName("pins")]
    public SortedDictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
