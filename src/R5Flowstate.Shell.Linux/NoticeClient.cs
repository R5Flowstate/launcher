using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>Legal notice payload, mirroring the Windows launcher's EulaResult.</summary>
public sealed class EulaResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string Contents { get; init; } = "";
    public int Version { get; init; }
    public string Lang { get; init; } = "english";

    public static EulaResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// The legal notice lane (POST /spire/notice?language=…): the payload type, its
/// parser, and the two URL helpers the rest of the shell uses. The transport
/// itself lives in <see cref="MasterServerClient"/>, which owns one HttpClient
/// for every master-server call, so this file is the EULA half of upstream's
/// MasterServerClient and nothing else.
///
/// The Servers tab is gated on accepting the notice this returns, so the request
/// shape (frozen empty JSON body, https only, versioned response) is identical
/// to the Windows launcher.
/// </summary>
public static class NoticeClient
{
    /// <summary>Same default the Windows build uses (ProductConstants).</summary>
    public const string DefaultMasterServerUrl = ProductConstants.DefaultMasterServerUrl;

    public static string NormalizeBaseUrl(string? raw) => MasterServerClient.NormalizeBaseUrl(raw);

    public static Task<EulaResult> GetEulaAsync(
        string? baseUrl,
        string? language = null,
        CancellationToken cancel = default)
        => MasterServerClient.GetEulaAsync(baseUrl, language, cancel);

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

            var contents = contentsEl.GetString() ?? "";
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

            return new EulaResult { Success = true, Contents = contents, Version = version, Lang = lang };
        }
    }
}
