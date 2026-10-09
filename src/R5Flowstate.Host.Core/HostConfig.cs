using System.Text.Json;
using System.Text.Json.Serialization;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Host;

/// <summary>
/// Per-install host settings. Lives under .r5f/, which no content track owns,
/// so updates never touch it. Secrets are not stored here: they go only into
/// the instance cfg the dedi reads.
/// </summary>
public sealed class HostConfig
{
    public const int CurrentSchema = 1;
    public const int MaxInstances = 16;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("ring")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HostRing Ring { get; set; } = HostRing.Live;

    /// <summary>Operator override for testing; must still pass the CHANNEL host allowlist.</summary>
    [JsonPropertyName("channel_url")]
    public string? ChannelUrl { get; set; }

    [JsonPropertyName("update_check_minutes")]
    public int UpdateCheckMinutes { get; set; } = 30;

    [JsonPropertyName("update_grace_minutes")]
    public int UpdateGraceMinutes { get; set; } = 5;

    /// <summary>Linux only: the wine binary that runs r5apex_ds.exe.</summary>
    [JsonPropertyName("wine")]
    public string? Wine { get; set; }

    [JsonPropertyName("instances")]
    public List<HostInstance> Instances { get; set; } = new();

    public string EffectiveChannelUrl =>
        string.IsNullOrWhiteSpace(ChannelUrl) ? HostRings.DedicatedChannelUrl(Ring) : ChannelUrl!;

    public HostInstance? Find(string name) =>
        Instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string HostDir(string installPath) =>
        Path.Combine(installPath, ProductConstants.ContentCacheDirName, "host");

    public static string ConfigPath(string installPath) =>
        Path.Combine(HostDir(installPath), "host.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HostConfig Load(string installPath)
    {
        var path = ConfigPath(installPath);
        if (!File.Exists(path))
            return new HostConfig();
        var cfg = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), Json) ?? new HostConfig();
        cfg.Instances.RemoveAll(i => !HostInstance.IsValidName(i.Name));
        return cfg;
    }

    public void Save(string installPath)
    {
        var path = ConfigPath(installPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class HostInstance
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "main";

    [JsonPropertyName("port")]
    public int Port { get; set; } = LaunchArgs.DefaultDediPort;

    [JsonPropertyName("playlist")]
    public string? Playlist { get; set; }

    [JsonPropertyName("map")]
    public string? Map { get; set; }

    [JsonPropertyName("visibility")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SpireVisibility Visibility { get; set; } = SpireVisibility.Public;

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("playlist_overrides")]
    public Dictionary<string, string> PlaylistOverrides { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("extra_args")]
    public string? ExtraArgs { get; set; }

    [JsonPropertyName("auto_restart")]
    public bool AutoRestart { get; set; } = true;

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 24 &&
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    public string CfgStem => "instance_" + Name.Replace('-', '_');
}
