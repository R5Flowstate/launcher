using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>Which download lanes the master server is publishing right now.</summary>
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
/// Port of the Windows launcher's MasterServerClient
/// (Shell/MasterServerClient.cs): POST /spire/hosts, POST /spire/notice,
/// GET /launcher/status, GET /site/posts. Never auth, ban, add, or ops.
///
/// The Windows class lives in a net8.0-windows project, so it cannot be
/// referenced from here; the request shapes are kept identical, including the
/// hand-built JSON version body, the https-or-loopback rule, the 10 s timeout
/// and the deliberate absence of retries. Errors come back as strings, never as
/// exceptions, so a down master server cannot take the UI with it.
/// </summary>
public static class MasterServerClient
{
    static readonly HttpClient s_http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "R5FlowstateLauncher");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }

    public static string NormalizeBaseUrl(string? raw)
    {
        var s = string.IsNullOrWhiteSpace(raw)
            ? ProductConstants.DefaultMasterServerUrl
            : raw.Trim();
        return s.TrimEnd('/');
    }

    /// <summary>POST /spire/hosts — the public server browser.</summary>
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
        // The payload type and its parser already live in NoticeClient, which is
        // the same lane; only the transport is shared here.
        var lang = NoticeLanguages.Sanitize(language);
        var posted = await PostFrozenAsync(
            baseUrl,
            "/spire/notice?language=" + Uri.EscapeDataString(lang),
            "{}",
            cancel).ConfigureAwait(false);
        if (!posted.Ok)
            return EulaResult.Fail(posted.Error ?? Loc.Get("ms_fetch_eula"));
        return NoticeClient.ParseEula(posted.Body);
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

        if (!IsAllowedScheme(uri))
            return DownloadStatus.Unreachable;

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

    /// <summary>Unauth GET on the play host. HTTPS, or loopback HTTP.</summary>
    public static async Task<(bool Ok, string Body, string? Error)> GetPublicAsync(
        string? baseUrl,
        string pathAndQuery,
        CancellationToken cancel = default)
    {
        var root = NormalizeBaseUrl(baseUrl);
        Uri uri;
        try
        {
            uri = new Uri(root + pathAndQuery, UriKind.Absolute);
        }
        catch (Exception ex)
        {
            return (false, "", "Bad master-server URL: " + ex.Message);
        }

        if (!IsAllowedScheme(uri))
            return (false, "", "Master server must be https (http is loopback-only).");

        try
        {
            using var resp = await s_http.GetAsync(uri, cancel).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, "", $"Master server HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return (true, text, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, "", "Request timed out.");
        }
        catch (Exception ex)
        {
            return (false, "", "Could not reach master server: " + ex.Message);
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

    sealed class Posted
    {
        public bool Ok { get; init; }
        public string Body { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    static async Task<Posted> PostFrozenAsync(
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

        if (!IsAllowedScheme(uri))
            return new Posted { Error = "Master server must be https (http is loopback-only)." };

        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await s_http.PostAsync(uri, content, cancel).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Posted { Error = $"Master server HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" };
            return new Posted { Ok = true, Body = text };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Posted { Error = "Request timed out." };
        }
        catch (Exception ex)
        {
            return new Posted { Error = "Could not reach master server: " + ex.Message };
        }
    }

    static bool IsAllowedScheme(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           || IsLoopbackHttp(uri);

    static bool IsLoopbackHttp(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The version goes into a POST body, so it is quoted by hand rather
    /// than through a serializer the master server version would have to match.</summary>
    static string JsonString(string value)
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
