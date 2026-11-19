using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public sealed record CuratedVersion(
    string Version,
    string Sha256,
    long Size,
    string DownloadUrl,
    DateTimeOffset ApprovedAt,
    bool Restart,
    IReadOnlyList<string> Deps);

public sealed record CuratedPackage(
    string FullName,
    string Owner,
    string Name,
    bool Official,
    string Description,
    IReadOnlyList<CuratedVersion> Versions)
{
    public CuratedVersion Newest => Versions[0];
}

public sealed record CuratedRevoked(string FullName, string Version, string Reason);

public sealed record CuratedPlugin(
    string Id,
    string Version,
    string Tier,
    string Realm,
    bool FirstParty,
    int Api,
    IReadOnlyList<string> Caps,
    string ArtifactSha256,
    long ArtifactSize,
    string SourceSha256,
    IReadOnlyList<string> Builders,
    IReadOnlyList<string> ApprovedBy,
    string ArtifactUrl)
{
    public bool ClientInstallable =>
        Realm is "client" or "both" && (Tier != "native" || FirstParty);
}

public sealed record CuratedPluginRevoked(string Id, string Version, string Reason);

public sealed record CuratedKey(string KeyId, string PublicPem);

public sealed class CuratedList
{
    readonly Dictionary<string, CuratedPackage> _byFull;
    readonly HashSet<string> _revoked;
    readonly HashSet<string> _pluginRevoked;
    readonly IReadOnlyList<CuratedPackage> _packagesS30;

    public CuratedList(long serial, DateTimeOffset issuedAt, DateTimeOffset expiresAt,
        IReadOnlyList<CuratedPackage> packages, IReadOnlyList<CuratedRevoked> revoked,
        IReadOnlyList<CuratedPlugin>? plugins = null, IReadOnlyList<CuratedPluginRevoked>? pluginsRevoked = null,
        IReadOnlyList<CuratedPackage>? packagesS30 = null)
    {
        _packagesS30 = packagesS30 ?? Array.Empty<CuratedPackage>();
        Serial = serial;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        Packages = packages;
        Revoked = revoked;
        Plugins = plugins ?? Array.Empty<CuratedPlugin>();
        PluginsRevoked = pluginsRevoked ?? Array.Empty<CuratedPluginRevoked>();
        _byFull = new Dictionary<string, CuratedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
            _byFull.TryAdd(p.FullName, p);
        _revoked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in revoked)
            _revoked.Add(r.FullName + "-" + r.Version);
        _pluginRevoked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in PluginsRevoked)
            _pluginRevoked.Add(r.Id + "@" + r.Version);
    }

    public long Serial { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public IReadOnlyList<CuratedPackage> Packages { get; }
    public IReadOnlyList<CuratedRevoked> Revoked { get; }
    public IReadOnlyList<CuratedPlugin> Plugins { get; }
    public IReadOnlyList<CuratedPluginRevoked> PluginsRevoked { get; }

    /// <summary>
    /// The list as one season sees it. S30.1 packages sit in their own array so launchers that predate it
    /// never offer them to S21; a list without that array has nothing for S30.1.
    /// </summary>
    public CuratedList ForBuild(GameBuild build) => build == GameBuild.S30
        ? new CuratedList(Serial, IssuedAt, ExpiresAt, _packagesS30, Revoked, Plugins, PluginsRevoked)
        : this;

    public CuratedPackage? Find(string fullName) =>
        _byFull.TryGetValue(fullName, out var p) ? p : null;

    public CuratedVersion? FindVersion(string fullName, string version)
    {
        var p = Find(fullName);
        return p?.Versions.FirstOrDefault(v => string.Equals(v.Version, version, StringComparison.Ordinal));
    }

    public bool IsRevoked(string fullName, string version) => _revoked.Contains(fullName + "-" + version);

    public bool IsPluginRevoked(string id, string version) => _pluginRevoked.Contains(id + "@" + version);

    public CuratedPlugin? FindPlugin(string id, string? version)
    {
        CuratedPlugin? best = null;
        foreach (var plugin in Plugins)
        {
            if (plugin.Id != id || IsPluginRevoked(plugin.Id, plugin.Version))
                continue;
            if (version is not null)
            {
                if (plugin.Version == version)
                    return plugin;
                continue;
            }

            if (best is null || ModVersion.Compare(plugin.Version, best.Version) > 0)
                best = plugin;
        }

        return best;
    }
}

public sealed class CuratedListException : Exception
{
    public CuratedListException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary><c>catalog_unavailable</c> to callers; the verifier's own codes stay in the log.</summary>
    public string Code { get; }
}

/// <summary>
/// The R5Flowstate-signed list of hand-reviewed mod versions. It is the only source of what may be
/// offered or installed: a package outside it is never listed, and a download must match the
/// sha256 it pins. There is no fallback to the raw Thunderstore listing.
/// </summary>
public static class CuratedCatalog
{
    public const string Url = "https://play.r5flowstate.org/mods/curated.json";
    public const string Community = "r5flowstate";
    public const int MaxEnvelopeBytes = 6 * 1024 * 1024;
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    public const int MaxPackages = 2048;
    public const int MaxVersions = 32;
    public const int MaxRevoked = 256;
    public const int MaxDeps = 32;
    public const int MinKeyBits = 3072;
    public const string CacheRelative = ".r5f/cache/curated.json";
    public const string SerialRelative = ".r5f/cache/curated.serial";
    public const string GameCopyRelative = "platform/plugins/curated.json";
    public const int MaxPlugins = 512;
    public const int MaxPluginsRevoked = 256;
    public const int MaxPluginReason = 200;

    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
    // The server signs for 72 h; the issue-time slack covers player clocks that run behind.
    static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(72) + TimeSpan.FromMinutes(5);
    static readonly TimeSpan MaxIssuedAhead = TimeSpan.FromDays(1);

    /// <summary>Two slots so a key can rotate: ship the new key here first, then switch the server.</summary>
    public static readonly IReadOnlyList<CuratedKey> PinnedKeys = new[]
    {
        new CuratedKey("cur1", CuratedKeys.Cur1),
    };

    static readonly Regex s_part = new(@"^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);
    static readonly Regex s_version = new(@"^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}$", RegexOptions.CultureInvariant);
    static readonly Regex s_sha = new(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static bool IsFullName(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        var dash = s.IndexOf('-');
        return dash > 0 && s_part.IsMatch(s[..dash]) && s_part.IsMatch(s[(dash + 1)..]);
    }

    public static bool IsVersion(string? s) => !string.IsNullOrEmpty(s) && s_version.IsMatch(s);

    public static bool IsDependencyPin(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        var last = s.LastIndexOf('-');
        return last > 0 && IsFullName(s[..last]) && IsVersion(s[(last + 1)..]);
    }

    /// <summary>
    /// Installable packages built from the curated list alone. <paramref name="listing"/> only adds
    /// display fields (icon, downloads, categories); a package it carries that the list does not is
    /// dropped. Dependency pins are minimums: each resolves to the newest curated version at or above it.
    /// </summary>
    public static IReadOnlyList<ModPackage> ToPackages(CuratedList curated, IReadOnlyList<ModPackage>? listing = null)
    {
        var live = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in listing ?? Array.Empty<ModPackage>())
        {
            var full = string.IsNullOrEmpty(p.FullName) ? p.Owner + "-" + p.Name : p.FullName;
            if (!string.IsNullOrEmpty(full))
                live.TryAdd(full, p);
        }

        var result = new List<ModPackage>(curated.Packages.Count);
        foreach (var pkg in curated.Packages)
        {
            live.TryGetValue(pkg.FullName, out var l);
            var versions = pkg.Versions.Select(v =>
            {
                var lv = l?.Versions.FirstOrDefault(x => string.Equals(x.VersionNumber, v.Version, StringComparison.Ordinal));
                return new ModPackageVersion
                {
                    VersionNumber = v.Version,
                    DownloadUrl = v.DownloadUrl,
                    FileSize = v.Size,
                    Dependencies = v.Deps.Select(d => ResolveDependency(curated, d)).ToList(),
                    Description = pkg.Description,
                    Downloads = lv?.Downloads ?? 0,
                    Uploaded = lv?.Uploaded ?? v.ApprovedAt,
                    Icon = lv?.Icon ?? string.Empty,
                };
            }).ToList();
            result.Add(new ModPackage
            {
                FullName = pkg.FullName,
                Owner = pkg.Owner,
                Name = pkg.Name,
                Description = pkg.Description.Length > 0 ? pkg.Description : l?.Description ?? string.Empty,
                IconUrl = l?.IconUrl ?? string.Empty,
                PackageUrl = l?.PackageUrl ?? string.Empty,
                RatingScore = l?.RatingScore ?? 0,
                IsPinned = l?.IsPinned ?? false,
                Updated = l?.Updated ?? pkg.Newest.ApprovedAt,
                Categories = l?.Categories ?? Array.Empty<string>(),
                Versions = versions,
            });
        }

        return result;
    }

    static string ResolveDependency(CuratedList curated, string pin)
    {
        if (!ModInstallPlanner.TrySplitPin(pin, out var full, out var wanted) || wanted is null)
            return pin;
        var pkg = curated.Find(full);
        if (pkg is null || pkg.Versions.Any(v => v.Version == wanted))
            return pin;
        var newest = pkg.Newest.Version;
        return ModVersion.Compare(newest, wanted) >= 0 ? full + "-" + newest : pin;
    }

    public static string CanonicalDownloadUrl(string owner, string name, string version) =>
        "https://thunderstore.io/package/download/" + owner + "/" + name + "/" + version + "/";

    /// <summary>Verifies an envelope and returns the list it carries. Throws with a short code.</summary>
    public static CuratedList Verify(byte[] envelope, IReadOnlyList<CuratedKey> keys, DateTimeOffset now, long minSerial) =>
        Verify(envelope, keys, now, minSerial, allowExpired: false, log: null, pluginKeys: null);

    public static CuratedList Verify(byte[] envelope, IReadOnlyList<CuratedKey> keys, DateTimeOffset now, long minSerial,
        Action<string>? log, IReadOnlyList<CuratedKey>? pluginKeys) =>
        Verify(envelope, keys, now, minSerial, allowExpired: false, log, pluginKeys);

    static CuratedList Verify(byte[] envelope, IReadOnlyList<CuratedKey> keys, DateTimeOffset now, long minSerial,
        bool allowExpired, Action<string>? log, IReadOnlyList<CuratedKey>? pluginKeys)
    {
        if (envelope.Length == 0 || envelope.Length > MaxEnvelopeBytes)
            throw new CuratedListException("bad_envelope", "envelope size is out of range");

        byte[] payload;
        byte[] sig;
        string keyId;
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                throw new CuratedListException("bad_envelope", "envelope version is not 1");
            keyId = root.GetProperty("key_id").GetString() ?? string.Empty;
            payload = Convert.FromBase64String(root.GetProperty("payload").GetString() ?? string.Empty);
            sig = Convert.FromBase64String(root.GetProperty("sig").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CuratedListException("bad_envelope", "envelope is malformed", ex);
        }

        if (payload.Length == 0 || payload.Length > MaxPayloadBytes)
            throw new CuratedListException("bad_envelope", "payload size is out of range");

        var key = keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
        if (key is null || string.IsNullOrWhiteSpace(key.PublicPem))
            throw new CuratedListException("unknown_key", "list is signed by an unknown key");

        using (var rsa = RSA.Create())
        {
            try
            {
                rsa.ImportFromPem(key.PublicPem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new CuratedListException("unknown_key", "pinned key is unreadable", ex);
            }

            if (rsa.KeySize < MinKeyBits)
                throw new CuratedListException("unknown_key", "pinned key is too small");
            if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CuratedListException("bad_signature", "list signature does not verify");
        }

        var list = ParsePayload(payload, log, pluginKeys ?? CuratedKeys.PluginKeys);
        if (list.ExpiresAt - list.IssuedAt > MaxLifetime)
            throw new CuratedListException("bad_payload", "list lifetime is longer than 72 hours");
        if (list.IssuedAt - now > MaxIssuedAhead)
            throw new CuratedListException("bad_payload", "list is issued in the future");
        if (allowExpired)
            return list;
        if (now >= list.ExpiresAt)
            throw new CuratedListException("expired", "list expired at " + list.ExpiresAt.ToString("u"));
        if (list.Serial < minSerial)
            throw new CuratedListException("serial_rollback", "list serial " + list.Serial + " is older than " + minSerial);
        return list;
    }

    static CuratedList ParsePayload(byte[] payload, Action<string>? log, IReadOnlyList<CuratedKey> pluginKeys)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.GetProperty("v").GetInt32() != 1)
                throw Bad("payload version is not 1");
            if (!string.Equals(root.GetProperty("community").GetString(), Community, StringComparison.Ordinal))
                throw Bad("list is for another community");
            var serial = root.GetProperty("serial").GetInt64();
            if (serial < 1)
                throw Bad("serial must be positive");
            var issued = ParseTime(root.GetProperty("issued_at"));
            var expires = ParseTime(root.GetProperty("expires_at"));
            if (expires <= issued)
                throw Bad("list expires before it is issued");

            var packages = ParsePackages(root.GetProperty("packages"));
            var packagesS30 = root.TryGetProperty("packages_s30", out var s30El)
                ? ParsePackages(s30El)
                : new List<CuratedPackage>();

            var rev = root.GetProperty("revoked");
            if (rev.ValueKind != JsonValueKind.Array || rev.GetArrayLength() > MaxRevoked)
                throw Bad("revoked is not a bounded array");
            var revoked = new List<CuratedRevoked>();
            foreach (var r in rev.EnumerateArray())
            {
                var full = r.GetProperty("full_name").GetString();
                var ver = r.GetProperty("version").GetString();
                if (!IsFullName(full) || !IsVersion(ver))
                    throw Bad("revoked entry is malformed");
                revoked.Add(new CuratedRevoked(full!, ver!, Text(r.GetProperty("reason").GetString(), 300)));
            }

            var plugins = Array.Empty<CuratedPlugin>();
            if (root.TryGetProperty("plugins", out var pluginEl))
                plugins = ParsePlugins(pluginEl, log, pluginKeys);
            var pluginsRevoked = Array.Empty<CuratedPluginRevoked>();
            if (root.TryGetProperty("plugins_revoked", out var pluginRevEl))
                pluginsRevoked = ParsePluginsRevoked(pluginRevEl);

            return new CuratedList(serial, issued, expires, packages, revoked, plugins, pluginsRevoked, packagesS30);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CuratedListException("bad_payload", "list payload is malformed: " + ex.Message, ex);
        }
    }

    static List<CuratedPackage> ParsePackages(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() > MaxPackages)
            throw Bad("packages is not a bounded array");
        var packages = new List<CuratedPackage>(el.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateArray())
        {
            var pkg = ParsePackage(p);
            if (!seen.Add(pkg.FullName))
                throw Bad("package listed twice: " + pkg.FullName);
            packages.Add(pkg);
        }

        return packages;
    }

    static CuratedPlugin[] ParsePlugins(JsonElement el, Action<string>? log, IReadOnlyList<CuratedKey> keys)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() > MaxPlugins)
            throw Bad("plugins is not a bounded array");
        var list = new List<CuratedPlugin>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in el.EnumerateArray())
        {
            if (!TryPlugin(entry, keys, log, out var plugin) || plugin is null)
                continue;
            var pin = plugin.Id + "@" + plugin.Version;
            if (!seen.Add(pin))
                throw Bad("plugin listed twice: " + pin);
            list.Add(plugin);
        }

        return list.ToArray();
    }

    static bool TryPlugin(JsonElement entry, IReadOnlyList<CuratedKey> keys, Action<string>? log, out CuratedPlugin? plugin)
    {
        plugin = null;
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("key_id", out var keyIdEl) || keyIdEl.ValueKind != JsonValueKind.String ||
            !string.Equals(keyIdEl.GetString(), "plg1", StringComparison.Ordinal) ||
            !entry.TryGetProperty("statement", out var statementEl) || statementEl.ValueKind != JsonValueKind.String ||
            !entry.TryGetProperty("sig", out var sigEl) || sigEl.ValueKind != JsonValueKind.String ||
            !entry.TryGetProperty("artifact_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
        {
            log?.Invoke("plugin entry refused: bad_statement");
            return false;
        }

        var statementText = statementEl.GetString() ?? string.Empty;
        var sigText = sigEl.GetString() ?? string.Empty;
        var url = urlEl.GetString() ?? string.Empty;
        if (statementText.Length is 0 or > 8192 || sigText.Length is 0 or > 2048 || url.Length is 0 or > 2048)
        {
            log?.Invoke("plugin entry refused: bad_statement");
            return false;
        }

        byte[] statement;
        byte[] sig;
        try
        {
            statement = Convert.FromBase64String(statementText);
            sig = Convert.FromBase64String(sigText);
        }
        catch (FormatException)
        {
            log?.Invoke("plugin entry refused: bad_statement");
            return false;
        }

        if (statement.Length > PluginStatement.MaxBytes || sig.Length > PluginStatement.MaxSigBytes)
        {
            log?.Invoke("plugin entry refused: bad_statement");
            return false;
        }

        var check = PluginStatement.Check(statement, sig, keys, log);
        if (!check.Ok || check.Plugin is null)
        {
            if (check.Code != "empty_key")
                log?.Invoke("plugin entry refused: " + check.Code);
            return false;
        }

        var info = check.Plugin;
        if (!PluginStatement.IsArtifactUrl(url, info.ArtifactSha256))
        {
            log?.Invoke("plugin entry refused: bad_url");
            return false;
        }

        plugin = new CuratedPlugin(info.Id, info.Version, info.Tier, info.Realm, info.FirstParty, info.Api, info.Caps,
            info.ArtifactSha256, info.ArtifactSize, info.SourceSha256, info.Builders, info.ApprovedBy, url);
        return true;
    }

    static CuratedPluginRevoked[] ParsePluginsRevoked(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() > MaxPluginsRevoked)
            throw Bad("plugins_revoked is not a bounded array");
        var list = new List<CuratedPluginRevoked>();
        foreach (var row in el.EnumerateArray())
        {
            var id = row.GetProperty("id").GetString();
            var version = row.GetProperty("version").GetString();
            var reason = row.GetProperty("reason").GetString() ?? string.Empty;
            if (!PluginStatement.IsId(id) || !PluginStatement.IsVersion(version) || !PluginReasonOk(reason))
                throw Bad("plugins_revoked entry is malformed");
            list.Add(new CuratedPluginRevoked(id!, version!, reason));
        }

        return list.ToArray();
    }

    static bool PluginReasonOk(string reason)
    {
        if (reason.Length is < 1 or > MaxPluginReason)
            return false;
        foreach (var c in reason)
        {
            if (c is < (char)0x20 or > (char)0x7E)
                return false;
        }

        return true;
    }

    static CuratedPackage ParsePackage(JsonElement p)
    {
        var full = p.GetProperty("full_name").GetString();
        var owner = p.GetProperty("owner").GetString() ?? string.Empty;
        var name = p.GetProperty("name").GetString() ?? string.Empty;
        if (!IsFullName(full) || !string.Equals(full, owner + "-" + name, StringComparison.Ordinal))
            throw Bad("package name is malformed");
        var official = p.GetProperty("official").GetBoolean();
        var description = Text(p.GetProperty("description").GetString(), 1024);

        var vers = p.GetProperty("versions");
        if (vers.ValueKind != JsonValueKind.Array || vers.GetArrayLength() is 0 or > MaxVersions)
            throw Bad("versions is not a bounded array: " + full);
        var versions = new List<CuratedVersion>();
        foreach (var v in vers.EnumerateArray())
        {
            var number = v.GetProperty("version").GetString();
            var sha = v.GetProperty("sha256").GetString();
            var size = v.GetProperty("size").GetInt64();
            var url = v.GetProperty("download_url").GetString();
            if (!IsVersion(number) || sha is null || !s_sha.IsMatch(sha) || size < 0 ||
                !string.Equals(url, CanonicalDownloadUrl(owner, name, number!), StringComparison.Ordinal))
                throw Bad("version entry is malformed: " + full);
            var deps = new List<string>();
            var depArr = v.GetProperty("deps");
            if (depArr.ValueKind != JsonValueKind.Array || depArr.GetArrayLength() > MaxDeps)
                throw Bad("deps is not a bounded array: " + full);
            foreach (var d in depArr.EnumerateArray())
            {
                var dep = d.GetString();
                if (!IsDependencyPin(dep))
                    throw Bad("dependency is malformed: " + full);
                deps.Add(dep!);
            }

            versions.Add(new CuratedVersion(number!, sha, size, url!, ParseTime(v.GetProperty("approved_at")),
                v.GetProperty("restart").GetBoolean(), deps));
        }

        versions.Sort((a, b) => ModVersion.Compare(b.Version, a.Version));
        return new CuratedPackage(full!, owner, name, official, description, versions);
    }

    static DateTimeOffset ParseTime(JsonElement e)
    {
        var s = e.GetString();
        if (string.IsNullOrEmpty(s) || !DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            throw Bad("timestamp is malformed");
        return t;
    }

    static string Text(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s)
        {
            if (sb.Length >= max)
                break;
            if (char.IsControl(c) || c == '|')
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    static CuratedListException Bad(string message) => new("bad_payload", message);

    public static async Task<byte[]> FetchAsync(string url, HttpMessageHandler? handler, CancellationToken cancel)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CuratedListException("bad_url", "list url must be https");

        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (handler is HttpClientHandler h)
            h.AllowAutoRedirect = false;

        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "R5Flowstate-Mods/1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(FetchTimeout);
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if ((int)response.StatusCode != 200)
            throw new CuratedListException("http", "list returned HTTP " + (int)response.StatusCode);
        if (response.Content.Headers.ContentLength is > MaxEnvelopeBytes)
            throw new CuratedListException("bad_envelope", "list is too large");

        await using var input = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxEnvelopeBytes)
                throw new CuratedListException("bad_envelope", "list is too large");
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Fresh list, else the cached last-good one while it still verifies and has not expired.
    /// Anything else throws <c>catalog_unavailable</c>; the caller then lists nothing.
    /// </summary>
    public static async Task<CuratedList> LoadAsync(
        string installRoot,
        bool refresh,
        CancellationToken cancel,
        HttpMessageHandler? handler = null,
        IReadOnlyList<CuratedKey>? keys = null,
        Func<DateTimeOffset>? clock = null,
        string? url = null,
        Action<string>? log = null,
        IReadOnlyList<CuratedKey>? pluginKeys = null,
        bool gameCopyOptional = false)
    {
        keys ??= PinnedKeys;
        pluginKeys ??= CuratedKeys.PluginKeys;
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var minSerial = ReadSerial(installRoot);
        var cache = CachePath(installRoot);
        var build = GameBuilds.Detect(installRoot);

        if (!refresh && cache is not null && File.Exists(cache) &&
            now - File.GetLastWriteTimeUtc(cache) < FreshFor &&
            TryCached(cache, keys, now, minSerial, log, pluginKeys) is { } fresh)
        {
            Accept(installRoot, fresh.Envelope, fresh.List, log, gameCopyOptional);
            return fresh.List.ForBuild(build);
        }

        Exception? fetchError;
        try
        {
            var bytes = await FetchAsync(url ?? Url, handler, cancel).ConfigureAwait(false);
            var list = Verify(bytes, keys, now, minSerial, log, pluginKeys);
            Persist(installRoot, bytes, list.Serial);
            Accept(installRoot, bytes, list, log, gameCopyOptional);
            return list.ForBuild(build);
        }
        catch (CuratedListException ex) when (ex.Code == "plugin_list_copy" && !cancel.IsCancellationRequested)
        {
            log?.Invoke("plugin list copy failed: " + ex.Message);
            throw;
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            fetchError = ex;
            log?.Invoke("curated list fetch failed: " + ex.Message);
        }

        // A failed refresh may already have advanced the floor. The cache has to
        // clear that floor or an older envelope would be accepted again.
        minSerial = ReadSerial(installRoot);
        if (cache is not null && File.Exists(cache) &&
            TryCached(cache, keys, now, minSerial, log, pluginKeys) is { } cached)
        {
            Accept(installRoot, cached.Envelope, cached.List, log, gameCopyOptional);
            return cached.List.ForBuild(build);
        }

        throw new CuratedListException("catalog_unavailable", "The mod list could not be checked.", fetchError);
    }

    /// <summary>
    /// The cached list even if it has expired, for its revoked entries only: when no current list
    /// can be checked, a version it revoked stays revoked. Never use it to list or install.
    /// </summary>
    public static CuratedList? LoadLastKnown(string installRoot, IReadOnlyList<CuratedKey>? keys = null,
        Func<DateTimeOffset>? clock = null)
    {
        var cache = CachePath(installRoot);
        if (cache is null)
            return null;
        try
        {
            if (!File.Exists(cache) || File.GetAttributes(cache).HasFlag(FileAttributes.ReparsePoint))
                return null;
            var info = new FileInfo(cache);
            if (info.Length <= 0 || info.Length > MaxEnvelopeBytes)
                return null;
            var now = (clock ?? (() => DateTimeOffset.UtcNow))();
            return Verify(File.ReadAllBytes(cache), keys ?? PinnedKeys, now, 0, allowExpired: true, log: null, pluginKeys: null);
        }
        catch (Exception ex) when (ex is CuratedListException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    readonly record struct CachedList(byte[] Envelope, CuratedList List);

    static CachedList? TryCached(string path, IReadOnlyList<CuratedKey> keys, DateTimeOffset now, long minSerial,
        Action<string>? log, IReadOnlyList<CuratedKey> pluginKeys)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return null;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxEnvelopeBytes)
                return null;
            var bytes = File.ReadAllBytes(path);
            return new CachedList(bytes, Verify(bytes, keys, now, minSerial, log, pluginKeys));
        }
        catch (Exception ex) when (ex is CuratedListException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke("cached curated list refused: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A caller that only needs the mod list passes <paramref name="gameCopyOptional"/>: a failed copy
    /// leaves the game with no plugin list, which authorizes nothing.
    /// </summary>
    static void Accept(string installRoot, byte[] envelope, CuratedList list, Action<string>? log, bool gameCopyOptional)
    {
        try
        {
            var off = ModRevocation.Reconcile(installRoot, list);
            if (off.Count > 0)
                log?.Invoke("mods turned off by review: " + string.Join(", ", off));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            log?.Invoke("revoked mod update failed: " + ex.Message);
        }

        try
        {
            PublishGameCopy(installRoot, envelope, list, log);
        }
        catch (CuratedListException ex) when (gameCopyOptional && ex.Code == "plugin_list_copy")
        {
            log?.Invoke(ex.Message);
        }
    }

    static void PublishGameCopy(string installRoot, byte[] envelope, CuratedList list, Action<string>? log)
    {
        if (!SafePath.TryJoin(installRoot, GameCopyRelative, out var path))
            throw new CuratedListException("plugin_list_copy", "plugin list path refused");
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            throw new CuratedListException("plugin_list_copy", "plugin list path refused");

        try
        {
            Directory.CreateDirectory(dir);
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("plugins directory is a reparse point");
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("plugin list copy is a reparse point");
            WriteAtomic(path, envelope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteGameCopy(installRoot);
            throw new CuratedListException("plugin_list_copy", "plugin list copy failed: " + ex.Message, ex);
        }

        try
        {
            var removed = PluginEnablement.Reconcile(installRoot, list);
            if (removed.Count > 0)
                log?.Invoke("plugins removed by review: " + string.Join(", ", removed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke("plugin enablement update failed: " + ex.Message);
        }
    }

    static string? CachePath(string installRoot) =>
        SafePath.TryJoin(installRoot, CacheRelative, out var p) ? p : null;

    public static long ReadSerial(string installRoot)
    {
        try
        {
            if (!SafePath.TryJoin(installRoot, SerialRelative, out var p) || !File.Exists(p))
                return 0;
            var text = File.ReadAllText(p).Trim();
            return text.Length <= 20 && long.TryParse(text, out var n) && n > 0 ? n : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static void Persist(string installRoot, byte[] envelope, long serial)
    {
        if (!SafePath.TryJoin(installRoot, CacheRelative, out var cache) ||
            !SafePath.TryJoin(installRoot, SerialRelative, out var serialPath))
            throw new CuratedListException("cache_write", "curated list cache path refused");
        var dir = Path.GetDirectoryName(cache);
        if (string.IsNullOrEmpty(dir))
            throw new CuratedListException("cache_write", "curated list cache path refused");

        try
        {
            Directory.CreateDirectory(dir);
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("curated cache directory is a reparse point");

            var prior = ReadSerial(installRoot);
            if (serial > prior)
                WriteAtomic(serialPath, Encoding.ASCII.GetBytes(serial.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            try
            {
                WriteAtomic(cache, envelope);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The floor moved and the envelope on disk is still the older one.
                // Drop the game copy so that older list cannot keep authorizing plugins.
                if (serial > prior)
                    TryDeleteGameCopy(installRoot);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CuratedListException("cache_write", "curated list cache write failed: " + ex.Message, ex);
        }
    }

    static void TryDeleteGameCopy(string installRoot)
    {
        try
        {
            if (!SafePath.TryJoin(installRoot, GameCopyRelative, out var path))
                return;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                return;
            if (!File.Exists(path))
                return;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
