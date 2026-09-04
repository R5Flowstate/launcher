using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// SHARE_MANIFEST.json — schema 1 (zip volumes) or schema 2 (7z multi-vol).
/// Written by the build manager.
/// </summary>
public sealed class ShareManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 2;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "share_pack";

    /// <summary>zip (schema 1) or 7z (schema 2).</summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("payload_bytes")]
    public long PayloadBytes { get; set; }

    [JsonPropertyName("zip_bytes")]
    public long ZipBytes { get; set; }

    [JsonPropertyName("archives")]
    public List<ShareArchive> Archives { get; set; } = new();

    [JsonPropertyName("volumes")]
    public List<ShareVolume> Volumes { get; set; } = new();

    [JsonPropertyName("settings")]
    public SharePackSettingsDto? Settings { get; set; }

    /// <summary>Relative path → first volume name that contains it.</summary>
    [JsonPropertyName("path_index")]
    public Dictionary<string, string>? PathIndex { get; set; }
}

public sealed class ShareArchive
{
    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    [JsonPropertyName("archive_base")]
    public string ArchiveBase { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "7z";

    [JsonPropertyName("volume_count")]
    public int VolumeCount { get; set; }

    [JsonPropertyName("volumes")]
    public List<string> Volumes { get; set; } = new();

    [JsonPropertyName("file_count")]
    public int FileCount { get; set; }

    [JsonPropertyName("payload_bytes")]
    public long PayloadBytes { get; set; }

    [JsonPropertyName("files")]
    public List<ShareFileEntry>? Files { get; set; }
}

public sealed class ShareVolume
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>Schema 1 size field; prefer <see cref="ZipBytes"/> when set (schema 2).</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("zip_bytes")]
    public long? ZipBytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>share_volume | 7z_volume</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "7z_volume";

    [JsonPropertyName("archive_base")]
    public string? ArchiveBase { get; set; }

    /// <summary>1-based part index (producer field <c>index</c>).</summary>
    [JsonPropertyName("index")]
    public int? PartIndex { get; set; }

    [JsonIgnore]
    public long EffectiveSize => ZipBytes ?? Size;
}

public sealed class ShareFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

public sealed class SharePackSettingsDto
{
    [JsonPropertyName("max_volume_bytes")]
    public long? MaxVolumeBytes { get; set; }

    [JsonPropertyName("compression")]
    public string? Compression { get; set; }

    [JsonPropertyName("compress_level")]
    public int? CompressLevel { get; set; }

    [JsonPropertyName("password_set")]
    public bool? PasswordSet { get; set; }

    [JsonPropertyName("include_sha256")]
    public bool? IncludeSha256 { get; set; }
}

public static class ShareManifestIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ShareManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ShareManifest>(json, JsonOptions)
                       ?? throw new InvalidOperationException($"Failed to parse SHARE_MANIFEST: {path}");
        return manifest;
    }

    public static void Save(string path, ShareManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }
}
