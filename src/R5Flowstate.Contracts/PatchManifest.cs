using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// PATCH_MANIFEST.json — delta identity written by the build manager.
/// Field names match the producer exactly.
/// </summary>
public sealed class PatchManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Relative path → new file entry for files not present in the parent.</summary>
    [JsonPropertyName("added")]
    public Dictionary<string, PatchFileEntry> Added { get; set; } = new();

    /// <summary>Relative path → new file entry (may include old_sha256) for changed files.</summary>
    [JsonPropertyName("changed")]
    public Dictionary<string, PatchFileEntry> Changed { get; set; } = new();

    /// <summary>Relative paths removed relative to the parent version.</summary>
    [JsonPropertyName("deleted")]
    public List<string> Deleted { get; set; } = new();

    [JsonPropertyName("unchanged_count")]
    public int UnchangedCount { get; set; }

    /// <summary>Content hash over the delta file map (added + changed), not the folded tip.</summary>
    [JsonPropertyName("content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("added_count")]
    public int AddedCount { get; set; }

    [JsonPropertyName("changed_count")]
    public int ChangedCount { get; set; }

    [JsonPropertyName("deleted_count")]
    public int DeletedCount { get; set; }

    [JsonPropertyName("total_delta_bytes")]
    public long TotalDeltaBytes { get; set; }
}

/// <summary>Per-file entry inside PATCH_MANIFEST added/changed maps.</summary>
public sealed class PatchFileEntry
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("mtime_ns")]
    public long? MtimeNs { get; set; }

    /// <summary>Present on changed entries only; parent sha256 before the patch.</summary>
    [JsonPropertyName("old_sha256")]
    public string? OldSha256 { get; set; }
}

public static class PatchManifestIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static PatchManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<PatchManifest>(json, JsonOptions)
                       ?? throw new InvalidOperationException($"Failed to parse PATCH_MANIFEST: {path}");
        return manifest;
    }

    public static void Save(string path, PatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }
}
