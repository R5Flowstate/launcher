using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Host;

/// <summary>What is installed, per track. Written only after a track settles.</summary>
public sealed class HostState
{
    [JsonPropertyName("tracks")]
    public Dictionary<string, HostTrackState> Tracks { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("gate_name")]
    public string? GateName { get; set; }

    [JsonPropertyName("marketing_tag")]
    public string? MarketingTag { get; set; }

    [JsonPropertyName("updated_utc")]
    public DateTimeOffset? UpdatedUtc { get; set; }

    public static string StatePath(string installPath) =>
        Path.Combine(HostConfig.HostDir(installPath), "state.json");

    public static HostState Load(string installPath)
    {
        var path = StatePath(installPath);
        if (!File.Exists(path))
            return new HostState();
        return JsonSerializer.Deserialize<HostState>(File.ReadAllText(path)) ?? new HostState();
    }

    public void Save(string installPath)
    {
        var path = StatePath(installPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class HostTrackState
{
    [JsonPropertyName("catalog_version")]
    public string? CatalogVersion { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("manifest_sha256")]
    public string? ManifestSha256 { get; set; }
}
