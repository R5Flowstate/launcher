using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.ModTool;

/// <summary>
/// <c>r5f-modtool.exe &lt;op&gt; --install-root &lt;dir&gt;</c>, run by the game. Stdout is newline-delimited JSON.
/// What may be offered or installed comes only from the R5Flowstate-signed curated list; the
/// Thunderstore listing adds display fields and nothing else. Downloads must match the sha256 the
/// list pins before anything is unpacked. The only write outside the tool's cache and log is
/// <c>.r5f/staged</c> and <c>.r5f/verified.json</c>; mods/ belongs to the game.
/// <c>review</c> and <c>review-spool</c> run on the master server's review box, never in the game.
/// </summary>
public static class ModTool
{
    public const int Protocol = 3;
    public const int MaxLineChars = 16 * 1024;
    public const string ListingCacheRelative = ".r5f/cache/thunderstore/listing.json";

    const int ExitOk = 0;
    const int ExitOther = 1;
    const int ExitBadArgs = 2;
    const int ExitRefused = 3;
    const int ExitNetwork = 4;

    internal static IReadOnlyList<CuratedKey> Keys = CuratedCatalog.PinnedKeys;
    internal static IReadOnlyList<CuratedKey> PluginKeys = CuratedKeys.PluginKeys;
    internal static string CuratedUrl = CuratedCatalog.Url;
    internal static Func<HttpMessageHandler?> CuratedHandler = () => null;
    internal static Func<HttpMessageHandler?> PluginHandler = () => null;
    internal static Func<ThunderstoreClient> NewThunderstore = () => new ThunderstoreClient();
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    static readonly Regex s_rootName = new(
        @"^[A-Za-z0-9_]{1,64}-[A-Za-z0-9_]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Regex s_digest = new(
        @"^[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly JsonSerializerOptions s_out = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin),
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    static readonly JsonSerializerOptions s_cache = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly object s_write = new();

    public static int Run(string[] args)
    {
        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        return Run(args, stdout);
    }

    public static int Run(string[] args, TextWriter stdout)
    {
        try
        {
            return RunAsync(args, stdout).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Finish(stdout, null, false, "error", ex.Message, ExitOther);
            return ExitOther;
        }
    }

    static async Task<int> RunAsync(string[] args, TextWriter stdout)
    {
        if (args.Length > 0 && args[0] == "plugins")
            return await PluginsAsync(args, stdout).ConfigureAwait(false);

        if (!TryParse(args, out var op, out var err))
            return Finish(stdout, null, false, "bad_args", err, ExitBadArgs);

        if (!IsKnownOp(op.Op))
            return Finish(stdout, null, false, "bad_op", "unknown op", ExitBadArgs);

        if (!string.IsNullOrEmpty(Unexpected(op)))
            return Finish(stdout, null, false, "bad_args", Unexpected(op), ExitBadArgs);

        if (op.Op == "review")
            return ReviewOp(stdout, op);
        if (op.Op == "review-spool")
            return ModReview.RunSpool(op.Spool!, line => Console.Error.WriteLine(Clean(line, 500)));

        if (!TryInstallRoot(op.InstallRoot, out var root, out var rootErr))
            return Finish(stdout, null, false, "bad_root", rootErr, ExitRefused);

        try
        {
            return op.Op switch
            {
                "hello" => Hello(stdout, root),
                "catalog" => await CatalogAsync(stdout, root, op.Refresh).ConfigureAwait(false),
                "installed" => await InstalledAsync(stdout, root).ConfigureAwait(false),
                "plan" => await PlanOpAsync(stdout, root, op.Roots).ConfigureAwait(false),
                "stage" => await StageOpAsync(stdout, root, op.Roots, op.Digest!).ConfigureAwait(false),
                _ => Finish(stdout, root, false, "bad_op", "unknown op", ExitBadArgs),
            };
        }
        catch (CuratedListException ex)
        {
            Log(root, "curated list: " + ex.Message + (ex.InnerException is null ? string.Empty : " (" + ex.InnerException.Message + ")"));
            return Finish(stdout, root, false, "catalog_unavailable", "The mod list could not be checked. Try again later.", ExitNetwork);
        }
        catch (ModHashMismatchException ex)
        {
            return Finish(stdout, root, false, "hash_mismatch", ex.Message, ExitRefused);
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
            return Finish(stdout, root, false, "network", ex.Message, ExitNetwork);
        }
        catch (Exception ex)
        {
            return Finish(stdout, root, false, "error", ex.Message, ExitOther);
        }
    }

    static int ReviewOp(TextWriter stdout, Parsed op)
    {
        var report = ModReview.Review(op.Zip!, op.FullName!, op.Version!, op.Work!);
        var json = ModReview.Serialize(report);
        if (op.Out is not null)
        {
            File.WriteAllText(op.Out, json);
            return report["ok"]?.GetValue<bool>() == true ? ExitOk : ExitRefused;
        }

        lock (s_write)
        {
            stdout.WriteLine(json);
        }

        return report["ok"]?.GetValue<bool>() == true ? ExitOk : ExitRefused;
    }

    static string ToolVersion()
    {
        var v = typeof(ModTool).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ModTool).Assembly.GetName().Version?.ToString()
                ?? "0";
        var plus = v.IndexOf('+');
        return Clean(plus >= 0 ? v[..plus] : v, 32);
    }

    static int Hello(TextWriter stdout, string root)
    {
        Emit(stdout, new { ev = "hello", protocol = Protocol, tool_version = ToolVersion() });
        return Finish(stdout, root, true, "ok", "hello", ExitOk);
    }

    static Task<CuratedList> LoadCuratedAsync(string root, bool refresh)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return CuratedCatalog.LoadAsync(root, refresh, cts.Token, CuratedHandler(), Keys, Clock, CuratedUrl,
            line => Log(root, line), PluginKeys);
    }

    static async Task<int> CatalogAsync(TextWriter stdout, string root, bool refresh)
    {
        var curated = await LoadCuratedAsync(root, refresh).ConfigureAwait(false);
        var listing = await ListingOrEmptyAsync(root, refresh).ConfigureAwait(false);
        foreach (var pkg in curated.Packages)
        {
            listing.TryGetValue(pkg.FullName, out var live);
            Emit(stdout, CatalogRow(pkg, live));
        }

        return Finish(stdout, root, true, "ok", "catalog", ExitOk);
    }

    static object CatalogRow(CuratedPackage pkg, ModPackage? live)
    {
        var ver = pkg.Newest;
        var cats = new List<string>();
        foreach (var cat in live?.Categories ?? Array.Empty<string>())
        {
            if (cats.Count >= 12)
                break;
            var clean = Clean(cat, 48);
            if (clean.Length > 0)
                cats.Add(clean);
        }

        var updated = live is null || live.Updated == default
            ? ver.ApprovedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            : live.Updated.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var description = pkg.Description.Length > 0 ? pkg.Description : live?.Description;
        return new
        {
            ev = "pkg",
            full_name = pkg.FullName,
            owner = Clean(pkg.Owner, 64),
            name = Clean(pkg.Name, 64),
            version = Clean(ver.Version, 64),
            description = Clean(description, ThunderstoreClient.MaxListingDescriptionChars),
            downloads = Math.Max(0L, live?.TotalDownloads ?? 0),
            rating = live?.RatingScore ?? 0,
            categories = cats,
            size = Math.Max(0L, ver.Size),
            deps = ver.Deps.Take(32).ToList(),
            updated,
            verified = true,
            official = pkg.Official,
            restart = ver.Restart,
        };
    }

    static async Task<Dictionary<string, ModPackage>> ListingOrEmptyAsync(string root, bool refresh)
    {
        try
        {
            return Index(await LoadListingAsync(root, refresh).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNetwork(ex) || ex is InvalidOperationException or JsonException or IOException)
        {
            Log(root, "listing unavailable; showing curated fields only: " + ex.Message);
            return new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        }
    }

    static async Task<int> InstalledAsync(TextWriter stdout, string root)
    {
        CuratedList? curated = null;
        try
        {
            curated = await LoadCuratedAsync(root, refresh: false).ConfigureAwait(false);
        }
        catch (CuratedListException ex)
        {
            Log(root, "installed: curated list unavailable: " + ex.Message);
        }

        var records = VerifiedInstalls.Read(root, line => Log(root, line));
        var revokedFrom = curated ?? CuratedCatalog.LoadLastKnown(root, Keys, Clock);
        foreach (var mod in ModsStore.Discover(root))
        {
            var full = string.IsNullOrEmpty(mod.ThunderstoreFullName) ? mod.FolderName : mod.ThunderstoreFullName;
            var have = string.IsNullOrEmpty(mod.ThunderstoreVersion) ? mod.Version : mod.ThunderstoreVersion;
            var pkg = curated?.Find(full);
            var catalogVersion = pkg?.Newest.Version ?? string.Empty;
            var update = pkg is not null && ModVersion.IsNewer(catalogVersion, have);
            var verified = VerifiedInstalls.IsVerified(root, mod, records, curated);
            var revoked = VerifiedInstalls.IsRevoked(mod, revokedFrom);

            Emit(stdout, new
            {
                ev = "mod",
                id = Clean(mod.Id, 64),
                name = Clean(mod.Name, 128),
                version = Clean(mod.Version, 64),
                author = Clean(mod.Author, 64),
                enabled = mod.Enabled,
                order = mod.Order,
                folder = Clean(mod.FolderName, 160),
                realm = Clean(mod.Realm, 32),
                has_scripts = mod.HasScripts,
                catalog_version = Clean(catalogVersion, 64),
                update,
                verified,
                revoked,
            });
        }

        return Finish(stdout, root, true, "ok", "installed", ExitOk);
    }

    static async Task<int> PlanOpAsync(TextWriter stdout, string root, List<string> roots)
    {
        var built = await BuildPlanAsync(root, roots, refresh: false).ConfigureAwait(false);
        if (built.ErrorCode is not null)
            return Finish(stdout, root, false, built.ErrorCode, built.ErrorMessage ?? built.ErrorCode, built.Exit);

        foreach (var step in built.Steps)
        {
            Emit(stdout, new
            {
                ev = "step",
                pin = step.Pin,
                action = step.Action,
                size = step.Size,
                verified = true,
                restart = step.Curated.Restart,
            });
        }

        Emit(stdout, new { ev = "plan", digest = built.Digest });
        return Finish(stdout, root, true, "ok", "plan", ExitOk);
    }

    static async Task<int> StageOpAsync(TextWriter stdout, string root, List<string> roots, string digest)
    {
        if (!s_digest.IsMatch(digest))
            return Finish(stdout, root, false, "bad_args", "digest must be 64 hex chars", ExitBadArgs);

        var built = await BuildPlanAsync(root, roots, refresh: false).ConfigureAwait(false);
        if (built.ErrorCode is not null)
            return Finish(stdout, root, false, built.ErrorCode, built.ErrorMessage ?? built.ErrorCode, built.Exit);

        if (!string.Equals(built.Digest, digest, StringComparison.OrdinalIgnoreCase))
            return Finish(stdout, root, false, "plan_changed", "plan digest does not match", ExitRefused);

        ModInstaller.SweepStagedTemp(root);

        using var client = NewThunderstore();
        var installer = new ModInstaller(client);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        foreach (var step in built.Steps)
        {
            if (step.Action == "keep")
                continue;

            var lastPhase = string.Empty;
            var lastPct = -1;
            var progress = new Progress<ContentInstallProgress>(p =>
            {
                var phase = MapPhase(p.Phase);
                if (phase is null)
                    return;
                var total = p.Total > 0 ? p.Total : step.Size;
                var done = p.Current < 0 ? 0 : p.Current;
                var pct = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                if (phase == lastPhase && pct == lastPct)
                    return;
                lastPhase = phase;
                lastPct = pct;
                Emit(stdout, new { ev = "progress", pin = step.Pin, phase, done, total });
            });

            var staged = await installer.StageAsync(root, step.Package, step.Version.VersionNumber, progress, cts.Token,
                    step.Curated.Sha256, step.Curated.Size)
                .ConfigureAwait(false);

            if (SafePath.TryJoin(root, ModInstaller.StagedRelative + "/" + staged.Folder, out var stagedDir))
                VerifiedInstalls.Record(root, staged.Folder, step.Pin, step.Curated.Sha256, stagedDir);

            Emit(stdout, new
            {
                ev = "staged",
                pin = step.Pin,
                action = step.Action,
                folder = staged.Folder,
                id = staged.Id,
                version = Clean(staged.Version, 64),
            });
        }

        return Finish(stdout, root, true, "ok", "stage", ExitOk);
    }

    sealed class BuiltPlan
    {
        public List<PlannedStep> Steps { get; } = new();
        public string Digest { get; set; } = string.Empty;
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int Exit { get; set; }
    }

    sealed class PlannedStep
    {
        public ModPackage Package { get; init; } = null!;
        public ModPackageVersion Version { get; init; } = null!;
        public CuratedVersion Curated { get; init; } = null!;
        public string Pin { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public long Size { get; init; }
    }

    static async Task<BuiltPlan> BuildPlanAsync(string root, List<string> roots, bool refresh)
    {
        var built = new BuiltPlan();
        if (roots.Count == 0)
        {
            built.ErrorCode = "bad_args";
            built.ErrorMessage = "missing --root";
            built.Exit = ExitBadArgs;
            return built;
        }

        if (roots.Count > ModInstallPlanner.MaxPackages)
        {
            built.ErrorCode = "too_many";
            built.ErrorMessage = "more than 64 roots";
            built.Exit = ExitRefused;
            return built;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = new List<string>();
        foreach (var name in roots)
        {
            if (!s_rootName.IsMatch(name))
            {
                built.ErrorCode = "bad_args";
                built.ErrorMessage = "root is not Owner-Name";
                built.Exit = ExitBadArgs;
                return built;
            }

            if (seen.Add(name))
                wanted.Add(name);
        }

        var curated = await LoadCuratedAsync(root, refresh).ConfigureAwait(false);
        var catalog = CuratedCatalog.ToPackages(curated);
        var byFull = Index(catalog);
        var selected = new List<(ModPackage Package, string? Version)>();
        foreach (var name in wanted)
        {
            if (!byFull.TryGetValue(name, out var pkg))
            {
                built.ErrorCode = "not_in_catalog";
                built.ErrorMessage = "package is not on the reviewed list";
                built.Exit = ExitRefused;
                return built;
            }

            selected.Add((pkg, null));
        }

        var unresolved = new List<string>();
        IReadOnlyList<ModInstallStep> plan;
        var installed = ModsStore.Discover(root);
        try
        {
            plan = ModInstallPlanner.Plan(catalog, selected, installed, unresolved);
        }
        catch (InvalidOperationException ex)
        {
            built.ErrorCode = "too_many";
            built.ErrorMessage = ex.Message;
            built.Exit = ExitRefused;
            return built;
        }

        if (unresolved.Count > 0)
        {
            built.ErrorCode = "dependency_unverified";
            built.ErrorMessage = "a required mod is not on the reviewed list: " + Clean(unresolved[0], 80);
            built.Exit = ExitRefused;
            return built;
        }

        foreach (var step in plan)
        {
            var pin = step.Pin;
            var action = ActionFor(step, installed);
            var cv = curated.FindVersion(step.Package.FullName, step.Version.VersionNumber);
            if (cv is null)
            {
                built.ErrorCode = "dependency_unverified";
                built.ErrorMessage = "planned version is not on the reviewed list";
                built.Exit = ExitRefused;
                return built;
            }

            if (!UrlIsThunderstore(step.Version.DownloadUrl) && action != "keep")
            {
                built.ErrorCode = "refused";
                built.ErrorMessage = "catalog download url was rejected";
                built.Exit = ExitRefused;
                return built;
            }

            if (!s_rootName.IsMatch(step.Package.Owner + "-" + step.Package.Name))
            {
                built.ErrorCode = "refused";
                built.ErrorMessage = "package folder name is not Owner-Name";
                built.Exit = ExitRefused;
                return built;
            }

            built.Steps.Add(new PlannedStep
            {
                Package = step.Package,
                Version = step.Version,
                Curated = cv,
                Pin = pin,
                Action = action,
                Size = Math.Max(0L, cv.Size),
            });
        }

        built.Digest = DigestOf(built.Steps);
        return built;
    }

    static string ActionFor(ModInstallStep step, IReadOnlyList<InstalledMod> installed)
    {
        var folder = step.Package.Owner + "-" + step.Package.Name;
        var have = installed.FirstOrDefault(m =>
            string.Equals(m.FolderName, folder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.FolderName, step.Package.FullName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.ThunderstoreFullName, step.Package.FullName, StringComparison.OrdinalIgnoreCase));
        if (have is null)
            return "install";

        var current = string.IsNullOrEmpty(have.ThunderstoreVersion) ? have.Version : have.ThunderstoreVersion;
        if (string.IsNullOrEmpty(current))
            return "update";
        if (ModVersion.Compare(current, step.Version.VersionNumber) >= 0)
            return "keep";
        return "update";
    }

    static string DigestOf(IReadOnlyList<PlannedStep> steps)
    {
        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            sb.Append(step.Pin);
            sb.Append('\t');
            sb.Append(step.Action);
            sb.Append('\t');
            sb.Append(step.Size);
            sb.Append('\t');
            sb.Append(step.Curated.Sha256);
            sb.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static async Task<IReadOnlyList<ModPackage>> LoadListingAsync(string root, bool refresh)
    {
        if (!refresh && TryReadCache(root, out var cached))
            return cached;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var client = NewThunderstore();
        var live = await client.ListPackagesAsync(cts.Token).ConfigureAwait(false);
        try
        {
            WriteCache(root, live);
        }
        catch (Exception ex)
        {
            Log(root, "catalog cache write failed: " + ex.Message);
        }

        return live;
    }

    static bool TryReadCache(string root, out IReadOnlyList<ModPackage> packages)
    {
        packages = Array.Empty<ModPackage>();
        if (!SafePath.TryJoin(root, ListingCacheRelative, out var path) || !File.Exists(path))
            return false;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            return false;

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        if (age >= TimeSpan.FromMinutes(10))
            return false;

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > ThunderstoreClient.MaxPackageListBytes)
                return false;
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<ModPackage>>(json, s_cache);
            if (list is null)
                return false;
            packages = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void WriteCache(string root, IReadOnlyList<ModPackage> packages)
    {
        if (!SafePath.TryJoin(root, ListingCacheRelative, out var path))
            throw new InvalidOperationException("Refusing catalog cache path.");
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("Refusing catalog cache path.");
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(packages, s_cache);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    static Dictionary<string, ModPackage> Index(IReadOnlyList<ModPackage> catalog)
    {
        var byFull = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in catalog)
        {
            if (pkg.IsNsfw || pkg.IsDeprecated)
                continue;
            var full = string.IsNullOrEmpty(pkg.FullName) ? pkg.Owner + "-" + pkg.Name : pkg.FullName;
            if (!s_rootName.IsMatch(full))
                continue;
            byFull.TryAdd(full, pkg);
        }

        return byFull;
    }

    static bool UrlIsThunderstore(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        try
        {
            ThunderstoreClient.EnsureThunderstoreHost(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool TryInstallRoot(string? path, out string full, out string error)
    {
        full = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            error = "install root is not an existing directory";
            return false;
        }

        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            error = "install root could not be resolved";
            return false;
        }

        if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
        {
            error = "install root is a reparse point";
            return false;
        }

        var dx11 = Path.Combine(full, "r5apex.exe");
        var dx12 = Path.Combine(full, "r5apex_dx12.exe");
        if (!File.Exists(dx11) && !File.Exists(dx12))
        {
            error = "install root has neither r5apex.exe nor r5apex_dx12.exe";
            return false;
        }

        return true;
    }

    static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0)
            return string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (sb.Length >= max)
                break;
            if (c < 32 || c == 127 || c == '|')
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    static string? MapPhase(string? phase) => phase switch
    {
        "download" => "download",
        "extract" => "extract",
        "check" => "verify",
        "verify" => "verify",
        "commit" => "install",
        "install" => "install",
        _ => null,
    };

    static bool IsNetwork(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
                return true;
        }

        // A refused package names Thunderstore in its message too; only the listing failure is network.
        return ex.Message.Contains("listing failed", StringComparison.OrdinalIgnoreCase);
    }

    static void Emit(TextWriter stdout, object payload)
    {
        var json = JsonSerializer.Serialize(payload, s_out);
        if (json.Length > MaxLineChars || json.IndexOf('\n') >= 0 || json.IndexOf('\r') >= 0)
            return;
        lock (s_write)
        {
            stdout.WriteLine(json);
        }
    }

    static int Finish(TextWriter stdout, string? root, bool ok, string code, string msg, int exit)
    {
        var clean = Clean(msg, 300);
        var payload = new { ev = "done", ok, code, msg = clean };
        var json = JsonSerializer.Serialize(payload, s_out);
        if (json.Length > MaxLineChars)
        {
            payload = new { ev = "done", ok, code, msg = "truncated" };
            json = JsonSerializer.Serialize(payload, s_out);
        }

        lock (s_write)
        {
            stdout.WriteLine(json);
        }

        if (!string.IsNullOrEmpty(root))
            Log(root, code + " " + clean);
        return exit;
    }

    static void Log(string root, string line)
    {
        try
        {
            if (!SafePath.TryJoin(root, ".r5f/logs/modtool.log", out var path))
                return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + " " + Clean(line, 500) + "\n");
        }
        catch
        {
            // Logging must not change the exit code.
        }
    }

    static async Task<int> PluginsAsync(string[] args, TextWriter stdout)
    {
        if (args.Length < 2)
            return Finish(stdout, null, false, "bad_args", "usage: plugins <list|install|enable|disable|uninstall> --install-root <dir>", ExitBadArgs);
        var verb = args[1];
        if (verb is not ("list" or "install" or "enable" or "disable" or "uninstall"))
            return Finish(stdout, null, false, "bad_op", "unknown plugins op", ExitBadArgs);

        string? rootArg = null;
        string? idArg = null;
        var refresh = false;
        for (var i = 2; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--refresh")
            {
                refresh = true;
                continue;
            }

            if (flag == "--install-root")
            {
                if (rootArg is not null || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return Finish(stdout, null, false, "bad_args", "missing --install-root", ExitBadArgs);
                rootArg = args[++i];
                continue;
            }

            if (flag.StartsWith("--", StringComparison.Ordinal))
                return Finish(stdout, null, false, "bad_args", "unknown argument", ExitBadArgs);
            if (idArg is not null)
                return Finish(stdout, null, false, "bad_args", "unexpected argument", ExitBadArgs);
            if (flag.Length > 80)
                return Finish(stdout, null, false, "bad_args", "plugin id is too long", ExitBadArgs);
            idArg = flag;
        }

        if (verb == "list" && idArg is not null)
            return Finish(stdout, null, false, "bad_args", "arguments do not match the op", ExitBadArgs);
        if (verb != "list" && string.IsNullOrWhiteSpace(idArg))
            return Finish(stdout, null, false, "bad_args", "missing plugin id", ExitBadArgs);
        if (!TryInstallRoot(rootArg, out var root, out var rootErr))
            return Finish(stdout, null, false, "bad_root", rootErr, ExitRefused);

        try
        {
            return verb switch
            {
                "list" => await PluginsListAsync(stdout, root, refresh).ConfigureAwait(false),
                "install" => await PluginsInstallAsync(stdout, root, idArg!, refresh).ConfigureAwait(false),
                "enable" => await PluginsEnableAsync(stdout, root, idArg!, refresh).ConfigureAwait(false),
                "disable" => PluginsDisable(stdout, root, idArg!),
                _ => PluginsUninstall(stdout, root, idArg!),
            };
        }
        catch (CuratedListException ex)
        {
            Log(root, "curated list: " + ex.Message);
            return Finish(stdout, root, false, "catalog_unavailable", "The mod list could not be checked. Try again later.", ExitNetwork);
        }
        catch (PluginInstallException ex)
        {
            return Finish(stdout, root, false, ex.Code, ex.Message, ex.Code == "network" ? ExitNetwork : ExitRefused);
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
            return Finish(stdout, root, false, "network", ex.Message, ExitNetwork);
        }
        catch (Exception ex)
        {
            return Finish(stdout, root, false, "error", ex.Message, ExitOther);
        }
    }

    static async Task<CuratedList> LoadPluginsListAsync(string root, bool refresh)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await CuratedCatalog.LoadAsync(root, refresh, cts.Token, CuratedHandler(), Keys, Clock, CuratedUrl,
            line => Log(root, line), PluginKeys).ConfigureAwait(false);
    }

    static async Task<int> PluginsListAsync(TextWriter stdout, string root, bool refresh)
    {
        var list = await LoadPluginsListAsync(root, refresh).ConfigureAwait(false);
        var enabled = new HashSet<string>(PluginEnablement.Read(root), StringComparer.Ordinal);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in list.Plugins)
        {
            var pin = plugin.Id + "@" + plugin.Version;
            shown.Add(pin);
            var removed = list.IsPluginRevoked(plugin.Id, plugin.Version);
            Emit(stdout, new
            {
                ev = "plugin",
                id = plugin.Id,
                version = plugin.Version,
                tier = plugin.Tier,
                realm = plugin.Realm,
                first_party = plugin.FirstParty,
                caps = plugin.Caps,
                enabled = enabled.Contains(pin) && !removed,
                installed = PluginInstaller.IsInstalled(root, plugin),
                removed,
            });
        }

        foreach (var file in PluginInstaller.Scan(root, line => Log(root, line)))
        {
            var pin = file.Id + "@" + file.Version;
            if (!shown.Add(pin))
                continue;
            Emit(stdout, new
            {
                ev = "plugin",
                id = file.Id,
                version = file.Version,
                tier = file.Tier,
                removed = true,
                reason = "Removed by review",
            });
        }

        return Finish(stdout, root, true, "ok", "plugins", ExitOk);
    }

    static bool TryPluginArg(string text, out string id, out string? version, out string error)
    {
        id = string.Empty;
        version = null;
        error = string.Empty;
        var at = text.IndexOf('@');
        if (at < 0)
        {
            id = text;
            if (!PluginStatement.IsId(id))
            {
                error = "plugin id is malformed";
                return false;
            }

            return true;
        }

        id = text[..at];
        version = text[(at + 1)..];
        if (!PluginStatement.IsId(id) || !PluginStatement.IsVersion(version))
        {
            error = "plugin id is malformed";
            return false;
        }

        return true;
    }

    static string? SelectCode(CuratedList list, string id, string? version, out CuratedPlugin? plugin)
    {
        plugin = null;
        if (version is not null && list.IsPluginRevoked(id, version))
            return "revoked";
        plugin = list.FindPlugin(id, version);
        if (plugin is not null)
            return null;
        if (version is not null)
            return "not_in_catalog";
        foreach (var item in list.Plugins)
        {
            if (item.Id == id)
                return "revoked";
        }

        return "not_in_catalog";
    }

    static async Task<int> PluginsInstallAsync(TextWriter stdout, string root, string idArg, bool refresh)
    {
        if (!TryPluginArg(idArg, out var id, out var version, out var error))
            return Finish(stdout, root, false, "bad_args", error, ExitBadArgs);
        var list = await LoadPluginsListAsync(root, refresh).ConfigureAwait(false);
        var code = SelectCode(list, id, version, out var plugin);
        if (code is not null || plugin is null)
            return Finish(stdout, root, false, code ?? "not_in_catalog", "plugin is not in the reviewed list", ExitRefused);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await PluginInstaller.InstallAsync(root, plugin, PluginHandler(), cts.Token, line => Log(root, line)).ConfigureAwait(false);
        Emit(stdout, new
        {
            ev = "plugin",
            id = plugin.Id,
            version = plugin.Version,
            tier = plugin.Tier,
            installed = true,
        });
        return Finish(stdout, root, true, "ok", "installed", ExitOk);
    }

    static async Task<int> PluginsEnableAsync(TextWriter stdout, string root, string idArg, bool refresh)
    {
        if (!TryPluginArg(idArg, out var id, out var version, out var error))
            return Finish(stdout, root, false, "bad_args", error, ExitBadArgs);
        var list = await LoadPluginsListAsync(root, refresh).ConfigureAwait(false);
        CuratedPlugin? plugin;
        if (version is null)
        {
            plugin = null;
            foreach (var item in list.Plugins)
            {
                if (item.Id != id || list.IsPluginRevoked(item.Id, item.Version))
                    continue;
                if (!PluginInstaller.IsInstalled(root, item))
                    continue;
                if (plugin is null || ModVersion.Compare(item.Version, plugin.Version) > 0)
                    plugin = item;
            }

            if (plugin is null)
            {
                var why = SelectCode(list, id, null, out _);
                var missing = why is "not_in_catalog" or "revoked" ? why : "not_installed";
                return Finish(stdout, root, false, missing, "plugin is not installed", ExitRefused);
            }
        }
        else
        {
            var why = SelectCode(list, id, version, out plugin);
            if (why is not null || plugin is null)
                return Finish(stdout, root, false, why ?? "not_in_catalog", "plugin is not in the reviewed list", ExitRefused);
        }

        var enable = PluginEnablement.TryEnable(root, plugin, list);
        if (enable is not null)
            return Finish(stdout, root, false, enable, "plugin was not enabled", ExitRefused);
        Emit(stdout, new { ev = "plugin", id = plugin.Id, version = plugin.Version, enabled = true });
        return Finish(stdout, root, true, "ok", "enabled", ExitOk);
    }

    static int PluginsDisable(TextWriter stdout, string root, string idArg)
    {
        if (!TryPluginArg(idArg, out var id, out var version, out var error))
            return Finish(stdout, root, false, "bad_args", error, ExitBadArgs);
        if (version is not null)
            id = idArg[..idArg.IndexOf('@')];
        PluginEnablement.Disable(root, id);
        Emit(stdout, new { ev = "plugin", id, enabled = false });
        return Finish(stdout, root, true, "ok", "disabled", ExitOk);
    }

    static int PluginsUninstall(TextWriter stdout, string root, string idArg)
    {
        if (!TryPluginArg(idArg, out var id, out _, out var error))
            return Finish(stdout, root, false, "bad_args", error, ExitBadArgs);
        var fail = PluginInstaller.Uninstall(root, id, line => Log(root, line));
        if (fail is not null)
            return Finish(stdout, root, false, "refused", fail, ExitRefused);
        Emit(stdout, new { ev = "plugin", id, installed = false });
        return Finish(stdout, root, true, "ok", "uninstalled", ExitOk);
    }

    sealed class Parsed
    {
        public string Op { get; set; } = string.Empty;
        public string? InstallRoot { get; set; }
        public bool Refresh { get; set; }
        public List<string> Roots { get; } = new();
        public string? Digest { get; set; }
        public string? Zip { get; set; }
        public string? FullName { get; set; }
        public string? Version { get; set; }
        public string? Work { get; set; }
        public string? Out { get; set; }
        public string? Spool { get; set; }
    }

    static readonly string[] s_valueFlags =
    {
        "--install-root", "--root", "--digest", "--zip", "--full-name", "--version", "--work", "--out", "--spool",
    };

    static bool TryParse(string[] args, out Parsed op, out string error)
    {
        op = new Parsed();
        error = string.Empty;
        if (args.Length < 1)
        {
            error = "usage: r5f-modtool <op> --install-root <dir>";
            return false;
        }

        op.Op = args[0];
        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--refresh")
            {
                op.Refresh = true;
                continue;
            }

            if (!s_valueFlags.Contains(flag))
            {
                error = "unknown argument";
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = "missing value";
                return false;
            }

            var val = args[++i];
            if (val.StartsWith("--", StringComparison.Ordinal))
            {
                error = "missing value";
                return false;
            }

            if (flag == "--root")
            {
                op.Roots.Add(val);
                continue;
            }

            string? existing = flag switch
            {
                "--install-root" => op.InstallRoot,
                "--digest" => op.Digest,
                "--zip" => op.Zip,
                "--full-name" => op.FullName,
                "--version" => op.Version,
                "--work" => op.Work,
                "--out" => op.Out,
                _ => op.Spool,
            };
            if (existing is not null)
            {
                error = "duplicate " + flag;
                return false;
            }

            switch (flag)
            {
            case "--install-root": op.InstallRoot = val; break;
            case "--digest": op.Digest = val; break;
            case "--zip": op.Zip = val; break;
            case "--full-name": op.FullName = val; break;
            case "--version": op.Version = val; break;
            case "--work": op.Work = val; break;
            case "--out": op.Out = val; break;
            default: op.Spool = val; break;
            }
        }

        if (op.Op is "review" or "review-spool")
            return true;

        if (string.IsNullOrWhiteSpace(op.InstallRoot))
        {
            error = "missing --install-root";
            return false;
        }

        return true;
    }

    static bool IsKnownOp(string op) =>
        op is "hello" or "catalog" or "installed" or "plan" or "stage" or "review" or "review-spool";

    static string Unexpected(Parsed op)
    {
        var reviewFlags = op.Zip is not null || op.FullName is not null || op.Version is not null ||
                          op.Work is not null || op.Out is not null;
        var gameFlags = op.InstallRoot is not null || op.Refresh || op.Roots.Count > 0 || op.Digest is not null;
        bool extra = op.Op switch
        {
            "hello" => op.Refresh || op.Roots.Count > 0 || op.Digest is not null || reviewFlags || op.Spool is not null,
            "catalog" => op.Roots.Count > 0 || op.Digest is not null || reviewFlags || op.Spool is not null,
            "installed" => op.Refresh || op.Roots.Count > 0 || op.Digest is not null || reviewFlags || op.Spool is not null,
            "plan" => op.Refresh || op.Digest is not null || op.Roots.Count == 0 || reviewFlags || op.Spool is not null,
            "stage" => op.Refresh || op.Digest is null || op.Roots.Count == 0 || reviewFlags || op.Spool is not null,
            "review" => gameFlags || op.Spool is not null || op.Zip is null || op.FullName is null ||
                        op.Version is null || op.Work is null,
            "review-spool" => gameFlags || reviewFlags || op.Spool is null,
            _ => true,
        };
        return extra ? "arguments do not match the op" : string.Empty;
    }
}
