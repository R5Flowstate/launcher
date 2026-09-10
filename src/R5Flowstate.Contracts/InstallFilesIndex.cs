using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// What we believe is on disk, and why. A cache and never an authority: if it is
/// missing, stale or unreadable the launcher rebuilds it by hashing, which is
/// also the migration path for an install made by the volume-era launcher.
/// </summary>
public sealed class InstallFilesIndex
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("manifest_id")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyName("manifest_sha256")]
    public string ManifestSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Identity of the volume this index describes. A drive-letter change, a
    /// restore onto a different disk, or a copied folder all change this, and
    /// discarding the whole index is the correct response to any of them.
    /// </summary>
    [JsonPropertyName("volume_serial")]
    public uint VolumeSerial { get; set; }

    [JsonPropertyName("install_path")]
    public string InstallPath { get; set; } = string.Empty;

    [JsonPropertyName("verified_utc")]
    public string? VerifiedUtc { get; set; }

    /// <summary>Rotating cursor for the background sweep.</summary>
    [JsonPropertyName("sweep_cursor")]
    public int SweepCursor { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, InstallFileState> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool DescribesSameTarget(string installPath, uint volumeSerial) =>
        VolumeSerial == volumeSerial &&
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(InstallPath ?? string.Empty),
            System.IO.Path.TrimEndingDirectorySeparator(installPath ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
}

public sealed class InstallFileState
{
    [JsonPropertyName("s")]
    public long Size { get; set; }

    /// <summary>
    /// Raw UTC FILETIME ticks, never a formatted date. Local time would shift
    /// twice a year and read differently across machines.
    /// </summary>
    [JsonPropertyName("m")]
    public long MTimeUtcTicks { get; set; }

    [JsonPropertyName("h")]
    public string? Sha256 { get; set; }

    /// <summary>When the hash was last confirmed against the bytes on disk.</summary>
    [JsonPropertyName("v")]
    public long VerifiedUtcTicks { get; set; }

    /// <summary>
    /// Set when the player's own edit is being kept. Verification reports it,
    /// repair leaves it alone, and it is never treated as corruption.
    /// </summary>
    [JsonPropertyName("e")]
    public bool PlayerEdited { get; set; }

    public bool StatMatches(long size, long mtimeTicks) =>
        Size == size && MTimeUtcTicks == mtimeTicks;
}

public static class InstallFilesIndexIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static InstallFilesIndex? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            var idx = JsonSerializer.Deserialize<InstallFilesIndex>(json, JsonOptions);
            if (idx is null || idx.Schema != InstallFilesIndex.CurrentSchema)
                return null;
            idx.Files ??= new Dictionary<string, InstallFileState>(StringComparer.OrdinalIgnoreCase);
            return idx;
        }
        catch
        {
            // A cache that cannot be read is simply a cache we do not have.
            return null;
        }
    }

    /// <summary>Write-then-rename; a torn index would force a needless full re-hash.</summary>
    public static void Save(string path, InstallFilesIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(index, JsonOptions);
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
