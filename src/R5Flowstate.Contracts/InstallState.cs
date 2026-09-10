using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// Local install marker. Play refused while Incomplete or hash mismatch.
/// Schema 1 with optional dual-track ready flags.
/// </summary>
public sealed class InstallState
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("sdk_version_expected")]
    public string SdkVersionExpected { get; set; } = string.Empty;

    [JsonPropertyName("client_catalog_version")]
    public string? ClientCatalogVersion { get; set; }

    [JsonPropertyName("server_catalog_version")]
    public string? ServerCatalogVersion { get; set; }

    [JsonPropertyName("client_content_hash")]
    public string? ClientContentHash { get; set; }

    [JsonPropertyName("server_content_hash")]
    public string? ServerContentHash { get; set; }

    [JsonPropertyName("client_ready")]
    public bool ClientReady { get; set; }

    [JsonPropertyName("server_ready")]
    public bool ServerReady { get; set; }

    [JsonPropertyName("platform_catalog_version")]
    public string? PlatformCatalogVersion { get; set; }

    [JsonPropertyName("platform_content_hash")]
    public string? PlatformContentHash { get; set; }

    [JsonPropertyName("platform_ready")]
    public bool PlatformReady { get; set; }

    [JsonPropertyName("platform_last_completed_step")]
    public string? PlatformLastCompletedStep { get; set; }

    // HD textures. HdEnabled is the player's choice and survives an HD-less
    // tip; HdReady only says the chosen set is on disk. The client auto-detects
    // HD from the core packs existing, so turning it off must delete them.
    [JsonPropertyName("hd_catalog_version")]
    public string? HdCatalogVersion { get; set; }

    [JsonPropertyName("hd_content_hash")]
    public string? HdContentHash { get; set; }

    [JsonPropertyName("hd_ready")]
    public bool HdReady { get; set; }

    [JsonPropertyName("hd_enabled")]
    public bool HdEnabled { get; set; }

    [JsonPropertyName("hd_last_completed_step")]
    public string? HdLastCompletedStep { get; set; }

    /// <summary>
    /// The overlay edit set the player last chose to keep, paired with the tip
    /// it was kept against. Matching both means the same question with the same
    /// answer, so it is not asked again; either side changing is a new question.
    /// </summary>
    [JsonPropertyName("overlay_keep_key")]
    public string? OverlayKeepKey { get; set; }

    [JsonPropertyName("incomplete")]
    public bool Incomplete { get; set; } = true;

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    [JsonPropertyName("install_path")]
    public string? InstallPath { get; set; }

    /// <summary>
    /// Last completed update step id for the client track (base:VER or patch:FROM-&gt;TO).
    /// Resume starts at the next step after this id.
    /// </summary>
    [JsonPropertyName("client_last_completed_step")]
    public string? ClientLastCompletedStep { get; set; }

    /// <summary>
    /// Last completed update step id for the server track (base:VER or patch:FROM-&gt;TO).
    /// </summary>
    [JsonPropertyName("server_last_completed_step")]
    public string? ServerLastCompletedStep { get; set; }

    /// <summary>Client play gate: complete, client track ready, no last error.</summary>
    [JsonIgnore]
    public bool AllowsPlayClient =>
        !Incomplete &&
        ClientReady &&
        string.IsNullOrWhiteSpace(LastError) &&
        (string.IsNullOrWhiteSpace(PlatformCatalogVersion) || PlatformReady);

    /// <summary>Dedicated play gate: complete, server track ready, no last error.</summary>
    [JsonIgnore]
    public bool AllowsPlayDedi =>
        !Incomplete &&
        ServerReady &&
        string.IsNullOrWhiteSpace(LastError);

    /// <summary>Legacy alias for <see cref="AllowsPlayClient"/>.</summary>
    [JsonIgnore]
    public bool AllowsPlay => AllowsPlayClient;
}

public static class InstallStateIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static InstallState Load(string path)
    {
        var json = File.ReadAllText(path);
        var state = JsonSerializer.Deserialize<InstallState>(json, JsonOptions)
                    ?? throw new InvalidOperationException($"Failed to parse INSTALL_STATE: {path}");
        return state;
    }

    /// <summary>
    /// Write-then-rename. A truncated INSTALL_STATE reads as Corrupted forever,
    /// and this is saved at every install checkpoint, so a kill mid-write would
    /// otherwise strand a complete install on the setup screen.
    /// </summary>
    public static void Save(string path, InstallState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(json);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
