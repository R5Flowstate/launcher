using System.Net.Http;
using System.Text;
using System.Text.Json;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public sealed class EulaResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Contents { get; init; } = string.Empty;
    public int Version { get; init; }
    public string Lang { get; init; } = "english";

    public static EulaResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class DownloadStatus
{
    public bool Setup { get; init; } = true;
    public bool Content { get; init; } = true;
    public bool Platform { get; init; } = true;

    /// <summary>
    /// Standalone dedicated-server package. Fails closed: an old master with no
    /// dedi field, or no answer at all, must not advertise a link that 404s.
    /// </summary>
    public bool Dedi { get; init; }

    /// <summary>
    /// False when /launcher/status was not read. PLAY must not require an
    /// update the player cannot apply; INSTALL still sees the lane defaults.
    /// </summary>
    public bool Reachable { get; init; } = true;

    public static DownloadStatus AllowAll { get; } = new();

    public static DownloadStatus Unreachable { get; } = new() { Reachable = false };

    public bool AnyLane => Content || Platform;
}

/// <summary>
/// POST /spire/hosts and /spire/notice, GET /launcher/status. Never auth, ban, add, or ops.
/// </summary>
public static class MasterServerClient
{
    private static readonly HttpClient s_http;

    static MasterServerClient()
    {
        s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "R5FlowstateLauncher");
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json");
    }

    public static string NormalizeBaseUrl(string? raw)
    {
        var s = string.IsNullOrWhiteSpace(raw)
            ? ProductConstants.DefaultMasterServerUrl
            : raw.Trim();
        return s.TrimEnd('/');
    }

    public static async Task<ServerListResult> ListServersAsync(
        string? baseUrl,
        string wireVersion,
        string? language = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(wireVersion))
            return ServerListResult.Fail(Loc.Get("ms_wire_empty"));

        var lang = NoticeLanguages.ForUi(language);
        var posted = await PostFrozenAsync(
            baseUrl,
            "/spire/hosts?language=" + Uri.EscapeDataString(lang),
            "{\"version\":" + JsonString(wireVersion) + "}",
            cancel).ConfigureAwait(false);
        if (!posted.Ok)
            return ServerListResult.Fail(posted.Error ?? Loc.Get("browser_failed"));
        return ServerListingParser.Parse(posted.Body);
    }

    /// <summary>Display-only. Never records accept. <paramref name="language"/> is allowlisted.</summary>
    public static async Task<EulaResult> GetEulaAsync(
        string? baseUrl,
        string? language = null,
        CancellationToken cancel = default)
    {
        var lang = NoticeLanguages.Sanitize(language);
        var posted = await PostFrozenAsync(
            baseUrl,
            "/spire/notice?language=" + Uri.EscapeDataString(lang),
            "{}",
            cancel).ConfigureAwait(false);
        if (!posted.Ok)
            return EulaResult.Fail(posted.Error ?? Loc.Get("ms_fetch_eula"));
        return ParseEula(posted.Body);
    }

    /// <summary>
    /// Public download lanes. Unreachable is fail-open for PLAY (do not require
    /// an update) and still leaves content/platform true so INSTALL can try CDN.
    /// </summary>
    public static async Task<DownloadStatus> GetDownloadStatusAsync(
        string? baseUrl,
        CancellationToken cancel = default)
    {
        var root = NormalizeBaseUrl(baseUrl);
        Uri uri;
        try
        {
            uri = new Uri(root + "/launcher/status", UriKind.Absolute);
        }
        catch
        {
            return DownloadStatus.Unreachable;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !IsLoopbackHttp(uri))
        {
            return DownloadStatus.Unreachable;
        }

        try
        {
            using var resp = await s_http.GetAsync(uri, cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return DownloadStatus.Unreachable;
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return ParseDownloadStatus(text);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DownloadStatus.Unreachable;
        }
        catch
        {
            return DownloadStatus.Unreachable;
        }
    }

    public static DownloadStatus ParseDownloadStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DownloadStatus.Unreachable;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DownloadStatus.Unreachable;
            var setup = ReadBool(root, "setup", ReadBool(root, "enabled", true));
            return new DownloadStatus
            {
                Reachable = true,
                Setup = setup,
                Content = ReadBool(root, "content", true),
                Platform = ReadBool(root, "platform", true),
                Dedi = ReadBool(root, "dedi", false),
            };
        }
        catch (JsonException)
        {
            return DownloadStatus.Unreachable;
        }
    }

    static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var el))
            return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static EulaResult ParseEula(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EulaResult.Fail("Empty EULA response.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return EulaResult.Fail("EULA response was not JSON: " + ex.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return EulaResult.Fail("EULA response was not an object.");

            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successEl)
                && successEl.ValueKind == JsonValueKind.False)
            {
                var err = "EULA not available.";
                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                    err = errEl.GetString() ?? err;
                return EulaResult.Fail(err);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return EulaResult.Fail("EULA response missing data.");

            if (!data.TryGetProperty("contents", out var contentsEl)
                || contentsEl.ValueKind != JsonValueKind.String)
                return EulaResult.Fail("EULA response missing contents.");

            var contents = contentsEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(contents))
                return EulaResult.Fail("EULA contents were empty.");

            var version = 0;
            if (data.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.Number)
                verEl.TryGetInt32(out version);

            if (version <= 0 || version > 1_000_000)
                return EulaResult.Fail("EULA version invalid.");

            var lang = "english";
            if (data.TryGetProperty("lang", out var langEl) && langEl.ValueKind == JsonValueKind.String)
            {
                var l = langEl.GetString();
                if (!string.IsNullOrWhiteSpace(l))
                    lang = l;
            }

            return new EulaResult
            {
                Success = true,
                Contents = contents,
                Version = version,
                Lang = lang,
            };
        }
    }

    private sealed class Posted
    {
        public bool Ok { get; init; }
        public string Body { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    private static async Task<Posted> PostFrozenAsync(
        string? baseUrl,
        string pathAndQuery,
        string jsonBody,
        CancellationToken cancel)
    {
        var root = NormalizeBaseUrl(baseUrl);
        Uri uri;
        try
        {
            uri = new Uri(root + pathAndQuery, UriKind.Absolute);
        }
        catch (Exception ex)
        {
            return new Posted { Error = "Bad master-server URL: " + ex.Message };
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !IsLoopbackHttp(uri))
        {
            return new Posted { Error = "Master server must be https (http is loopback-only)." };
        }

        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await s_http.PostAsync(uri, content, cancel).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new Posted
                {
                    Error = $"Master server HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                };
            }

            return new Posted { Ok = true, Body = text };
        }
        catch (OperationCanceledException)
        {
            return new Posted { Error = "Request cancelled." };
        }
        catch (Exception ex)
        {
            return new Posted { Error = "Could not reach master server: " + ex.Message };
        }
    }

    private static bool IsLoopbackHttp(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string JsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
