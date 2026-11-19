using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace R5Flowstate.Content;

public sealed record PluginInfo(
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
    IReadOnlyList<string> ApprovedBy);

public readonly record struct PluginCheck(bool Ok, string Code, PluginInfo? Plugin);

/// <summary>
/// Signed plugin statement. The signature covers the exact statement bytes.
/// </summary>
public static class PluginStatement
{
    public const int MaxBytes = 4096;
    public const int ApiVersion = 1;
    public const long WasmMaxBytes = 8 * 1024 * 1024;
    public const long NativeMaxBytes = 16 * 1024 * 1024;
    public const int MaxCaps = 8;
    public const int MaxSigBytes = 1024;

    // Content objects are fetched from this host (channel and dedi packages).
    public const string CasHost = "cdn.r5flowstate.org";

    public static readonly string[] CapNames =
    {
        "log", "convars", "script", "events", "usercmd", "entities", "net", "storage",
    };

    static readonly string[] s_order =
    {
        "v", "id", "version", "tier", "realm", "first_party", "api", "caps",
        "artifact_sha256", "artifact_size", "source_sha256", "builders", "approved_by",
    };

    // $ also matches before a final newline, so these anchors are the end of the string.
    static readonly Regex s_id = new(@"^[a-z0-9_]{3,32}\z", RegexOptions.CultureInvariant);
    static readonly Regex s_version = new(@"^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}\z", RegexOptions.CultureInvariant);
    static readonly Regex s_sha = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    static readonly Regex s_name = new(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant);

    static int s_emptyNoted;

    internal static void ResetEmptyKeyNotice() => Interlocked.Exchange(ref s_emptyNoted, 0);

    public static bool IsId(string? s) => !string.IsNullOrEmpty(s) && s_id.IsMatch(s);

    public static bool IsVersion(string? s) => !string.IsNullOrEmpty(s) && s_version.IsMatch(s);

    public static bool IsPin(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        var at = s.IndexOf('@');
        return at > 0 && IsId(s[..at]) && IsVersion(s[(at + 1)..]);
    }

    public static byte[] Format(
        int v,
        string id,
        string version,
        string tier,
        string realm,
        bool firstParty,
        int api,
        IReadOnlyList<string> caps,
        string artifactSha256,
        long artifactSize,
        string sourceSha256,
        IReadOnlyList<string> builders,
        IReadOnlyList<string> approvedBy)
    {
        var sb = new StringBuilder(512);
        sb.Append("{\"v\":").Append(v.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"id\":");
        AppendString(sb, id);
        sb.Append(",\"version\":");
        AppendString(sb, version);
        sb.Append(",\"tier\":");
        AppendString(sb, tier);
        sb.Append(",\"realm\":");
        AppendString(sb, realm);
        sb.Append(",\"first_party\":").Append(firstParty ? "true" : "false");
        sb.Append(",\"api\":").Append(api.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"caps\":");
        AppendArray(sb, caps);
        sb.Append(",\"artifact_sha256\":");
        AppendString(sb, artifactSha256);
        sb.Append(",\"artifact_size\":").Append(artifactSize.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"source_sha256\":");
        AppendString(sb, sourceSha256);
        sb.Append(",\"builders\":");
        AppendArray(sb, builders);
        sb.Append(",\"approved_by\":");
        AppendArray(sb, approvedBy);
        sb.Append('}');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static PluginCheck Check(byte[] statement, byte[] signature, IReadOnlyList<CuratedKey>? keys, Action<string>? log)
    {
        keys ??= CuratedKeys.PluginKeys;
        try
        {
            if (!TryParse(statement, out var info, out var code))
                return new PluginCheck(false, code, null);
            if (!TryVerify(statement, signature, keys, log, out var sigCode))
                return new PluginCheck(false, sigCode, null);
            return new PluginCheck(true, "ok", info);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return new PluginCheck(false, "bad_statement", null);
        }
    }

    public static bool IsArtifactUrl(string? url, string sha256)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || string.IsNullOrEmpty(sha256))
            return false;
        if (url.Contains('\\') || url.Contains("..", StringComparison.Ordinal) ||
            url.Contains('%') || url.Contains('?') || url.Contains('#') || url.Contains('@') ||
            url.Contains(' ') || url.Contains('\n') || url.Contains('\r'))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (!string.Equals(uri.Host, CasHost, StringComparison.OrdinalIgnoreCase))
            return false;
        if (uri.Port != 443)
            return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        if (slash < 0 || slash == path.Length - 1)
            return false;
        var leaf = path[(slash + 1)..];
        if (string.Equals(leaf, sha256, StringComparison.Ordinal))
            return true;
        var dot = sha256.Length;
        if (leaf.Length <= dot + 1 || leaf[dot] != '.')
            return false;
        if (!leaf.StartsWith(sha256, StringComparison.Ordinal))
            return false;
        var ext = leaf[(dot + 1)..];
        if (ext.Length is < 1 or > 8)
            return false;
        foreach (var c in ext)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }

    public static bool ClientInstallable(PluginInfo plugin) =>
        plugin.Realm is "client" or "both" && (plugin.Tier != "native" || plugin.FirstParty);

    static bool TryParse(byte[] statement, out PluginInfo? info, out string code)
    {
        info = null;
        code = "bad_statement";
        if (statement.Length == 0 || statement.Length > MaxBytes)
            return false;
        foreach (var b in statement)
        {
            if (b is < 32 or > 126)
                return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(statement);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var seen = 0;
            foreach (var prop in root.EnumerateObject())
            {
                if (seen >= s_order.Length || !string.Equals(prop.Name, s_order[seen], StringComparison.Ordinal))
                    return false;
                seen++;
            }

            if (seen != s_order.Length)
                return false;
            if (!root.GetProperty("v").TryGetInt32(out var v))
                return false;
            var id = root.GetProperty("id").GetString();
            var version = root.GetProperty("version").GetString();
            var tier = root.GetProperty("tier").GetString();
            var realm = root.GetProperty("realm").GetString();
            if (root.GetProperty("first_party").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            var firstParty = root.GetProperty("first_party").GetBoolean();
            if (!root.GetProperty("api").TryGetInt32(out var api))
                return false;
            if (!TryStrings(root.GetProperty("caps"), 9, out var caps))
                return false;
            var artifactSha = root.GetProperty("artifact_sha256").GetString();
            if (!root.GetProperty("artifact_size").TryGetInt64(out var artifactSize))
                return false;
            var sourceSha = root.GetProperty("source_sha256").GetString();
            if (!TryStrings(root.GetProperty("builders"), 3, out var builders))
                return false;
            if (!TryStrings(root.GetProperty("approved_by"), 5, out var approved))
                return false;

            if (id is null || version is null || tier is null || realm is null || artifactSha is null || sourceSha is null)
                return false;

            var canonical = Format(v, id, version, tier, realm, firstParty, api, caps, artifactSha, artifactSize,
                sourceSha, builders, approved);
            if (!canonical.AsSpan().SequenceEqual(statement))
                return false;

            if (v != 1 || !IsId(id) || !IsVersion(version))
                return false;
            if (tier is not ("wasm" or "native") || realm is not ("client" or "server" or "both"))
                return false;
            if (api < 1 || api > ApiVersion)
                return false;
            if (!CapsOk(caps))
                return false;
            if (!s_sha.IsMatch(artifactSha) || !s_sha.IsMatch(sourceSha))
                return false;
            var max = tier == "native" ? NativeMaxBytes : WasmMaxBytes;
            if (artifactSize < 1 || artifactSize > max)
                return false;
            if (builders.Count != 2 || !DistinctNames(builders))
                return false;
            if (approved.Count is < 1 or > 4 || !DistinctNames(approved))
                return false;
            if (tier == "native" && realm is "client" or "both" && !firstParty)
                return false;

            info = new PluginInfo(id, version, tier, realm, firstParty, api, caps, artifactSha, artifactSize,
                sourceSha, builders, approved);
            code = "ok";
            return true;
        }
    }

    static bool CapsOk(IReadOnlyList<string> caps)
    {
        if (caps.Count > MaxCaps)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cap in caps)
        {
            if (!seen.Add(cap))
                return false;
            if (Array.IndexOf(CapNames, cap) < 0)
                return false;
        }

        return true;
    }

    static bool DistinctNames(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!s_name.IsMatch(name) || !seen.Add(name))
                return false;
        }

        return true;
    }

    static bool TryStrings(JsonElement el, int maxRead, out List<string> values)
    {
        values = new List<string>();
        if (el.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in el.EnumerateArray())
        {
            if (values.Count >= maxRead)
                return false;
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var s = item.GetString();
            if (s is null)
                return false;
            values.Add(s);
        }

        return true;
    }

    static bool TryVerify(byte[] statement, byte[] signature, IReadOnlyList<CuratedKey> keys, Action<string>? log, out string code)
    {
        code = "bad_signature";
        if (signature.Length == 0 || signature.Length > MaxSigBytes)
            return false;

        var anyPem = false;
        foreach (var key in keys)
        {
            if (!string.Equals(key.KeyId, "plg1", StringComparison.Ordinal))
                continue;
            if (string.IsNullOrWhiteSpace(key.PublicPem))
                continue;
            anyPem = true;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(key.PublicPem);
                if (rsa.KeySize < CuratedCatalog.MinKeyBits)
                    continue;
                if (rsa.VerifyData(statement, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    code = "ok";
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                continue;
            }
        }

        if (!anyPem)
        {
            code = "empty_key";
            if (log is not null && Interlocked.CompareExchange(ref s_emptyNoted, 1, 0) == 0)
                log("plg1 public key is empty; plugin statements will not verify");
        }

        return false;
    }

    static void AppendArray(StringBuilder sb, IReadOnlyList<string> values)
    {
        sb.Append('[');
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            AppendString(sb, values[i]);
        }

        sb.Append(']');
    }

    static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}
