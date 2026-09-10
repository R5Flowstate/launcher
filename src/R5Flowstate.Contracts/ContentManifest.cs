using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// CONTENT_MANIFEST v1. The file list IS the version: an install is correct when
/// every entry matches and nothing the manifest does not list survives under a
/// root it owns. Written by the build manager.
/// </summary>
public sealed class ContentManifest
{
    public const int CurrentSchema = 1;
    public const string ExpectedKind = "content_manifest";

    /// <summary>sha256 of zero bytes. A live platform tip shipped this as its content hash.</summary>
    public const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExpectedKind;

    [JsonPropertyName("manifest_id")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = string.Empty;

    [JsonPropertyName("catalog_version")]
    public string CatalogVersion { get; set; } = string.Empty;

    [JsonPropertyName("created_utc")]
    public string? CreatedUtc { get; set; }

    [JsonPropertyName("hash_algo")]
    public string HashAlgo { get; set; } = "sha256";

    [JsonPropertyName("cas_prefix")]
    public string CasPrefix { get; set; } = "cas/";

    [JsonPropertyName("chunk_size_default")]
    public long ChunkSizeDefault { get; set; }

    [JsonPropertyName("file_count")]
    public int FileCount { get; set; }

    [JsonPropertyName("payload_bytes")]
    public long PayloadBytes { get; set; }

    [JsonPropertyName("object_count")]
    public int ObjectCount { get; set; }

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ContentFile> Files { get; set; } = new();

    public string ObjectKey(string digest) =>
        $"{CasPrefix.TrimEnd('/')}/{digest[..2]}/{digest}";

    /// <summary>Every cas digest this manifest references, deduped.</summary>
    public IReadOnlyCollection<string> ObjectDigests()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Files)
        {
            if (f.Chunks is { Count: > 0 })
            {
                foreach (var c in f.Chunks)
                {
                    if (!string.IsNullOrEmpty(c))
                        set.Add(c);
                }
            }
            else if (!string.IsNullOrEmpty(f.Sha256))
            {
                set.Add(f.Sha256);
            }
        }
        return set;
    }

    /// <summary>
    /// Refuse a manifest before a single byte is fetched. Everything here is a
    /// shape that would otherwise produce a repair loop that never converges.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Schema != CurrentSchema)
            problems.Add($"schema is {Schema}, expected {CurrentSchema}");
        if (!string.Equals(Kind, ExpectedKind, StringComparison.Ordinal))
            problems.Add($"kind is '{Kind}', expected '{ExpectedKind}'");
        if (!string.Equals(HashAlgo, "sha256", StringComparison.OrdinalIgnoreCase))
            problems.Add($"unsupported hash_algo '{HashAlgo}'");

        if (string.IsNullOrWhiteSpace(ContentHash))
            problems.Add("content_hash is empty");
        else if (string.Equals(ContentHash, EmptySha256, StringComparison.OrdinalIgnoreCase))
            problems.Add("content_hash is the digest of the empty string; the gate would be inert");

        if (Files.Count == 0)
            problems.Add("no files");

        var seenLower = new Dictionary<string, string>(StringComparer.Ordinal);
        var sizeByHash = new Dictionary<string, long>(StringComparer.Ordinal);
        long total = 0;

        foreach (var f in Files)
        {
            var p = f.Path ?? string.Empty;
            total += f.Size;

            if (!SafePath.IsSafeRelative(p, out var reason))
                problems.Add($"'{p}': {reason}");

            var low = p.ToLowerInvariant();
            if (seenLower.TryGetValue(low, out var other) && !string.Equals(other, p, StringComparison.Ordinal))
                problems.Add($"'{p}' collides case-insensitively with '{other}'");
            seenLower[low] = p;

            if (string.IsNullOrEmpty(f.Sha256))
            {
                problems.Add($"'{p}': missing hash");
            }
            else if (!IsSha256Hex(f.Sha256))
            {
                problems.Add($"'{p}': hash is not lowercase sha256 hex");
            }
            else
            {
                if (sizeByHash.TryGetValue(f.Sha256, out var prev) && prev != f.Size)
                    problems.Add($"hash {f.Sha256[..12]} maps to both {prev} and {f.Size} bytes");
                sizeByHash[f.Sha256] = f.Size;
            }

            if (f.Chunks is { Count: > 0 })
            {
                if (f.ChunkSize <= 0)
                {
                    problems.Add($"'{p}': chunk list without a chunk size");
                }
                else
                {
                    var want = ChunkCount(f.Size, f.ChunkSize);
                    if (f.Chunks.Count != want)
                    {
                        problems.Add(
                            $"'{p}': {f.Chunks.Count} chunks, expected {want} for {f.Size} bytes at {f.ChunkSize}");
                    }
                    foreach (var c in f.Chunks)
                    {
                        if (!IsSha256Hex(c))
                        {
                            problems.Add($"'{p}': malformed chunk hash");
                            break;
                        }
                    }
                }
            }
            else if (f.ChunkSize > 0 && f.Size > f.ChunkSize)
            {
                problems.Add($"'{p}': larger than its chunk size but has no chunk list");
            }
        }

        if (PayloadBytes > 0 && PayloadBytes != total)
            problems.Add($"payload_bytes {PayloadBytes} but files sum to {total}");
        if (FileCount != Files.Count)
            problems.Add("file_count disagrees with the file list");

        return problems;
    }

    public static int ChunkCount(long size, long chunkSize)
    {
        if (size <= 0 || chunkSize <= 0)
            return 0;
        return (int)((size + chunkSize - 1) / chunkSize);
    }

    static bool IsSha256Hex(string? s)
    {
        if (s is null || s.Length != 64)
            return false;
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
                return false;
        }
        return true;
    }
}

public sealed class ContentFile
{
    [JsonPropertyName("p")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("s")]
    public long Size { get; set; }

    [JsonPropertyName("h")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Per file, so the producer can change it without re-uploading.</summary>
    [JsonPropertyName("cs")]
    public long ChunkSize { get; set; }

    [JsonPropertyName("ch")]
    public List<string>? Chunks { get; set; }

    [JsonIgnore]
    public bool IsChunked => Chunks is { Count: > 0 };

    public long ChunkOffset(int index) => (long)index * ChunkSize;

    public long ChunkLength(int index)
    {
        var off = ChunkOffset(index);
        var remain = Size - off;
        return remain < ChunkSize ? remain : ChunkSize;
    }
}

public static class ContentManifestIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ContentManifest Parse(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<ContentManifest>(utf8, JsonOptions)
        ?? throw new InvalidOperationException("Failed to parse CONTENT_MANIFEST.");

    public static ContentManifest Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(StripBom(bytes));
    }

    /// <summary>
    /// Parse and verify in one step. The digest is the manifest's authority: it
    /// comes from the CHANNEL, and every object inherits trust through it.
    /// </summary>
    public static ContentManifest LoadVerified(string path, string? expectedSha256)
    {
        var bytes = File.ReadAllBytes(path);
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var want = expectedSha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, want, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CONTENT_MANIFEST sha256 mismatch (expected {want}, got {actual}).");
            }
        }

        var manifest = Parse(StripBom(bytes));
        var problems = manifest.Validate();
        if (problems.Count > 0)
        {
            var head = string.Join("; ", problems.Take(4));
            throw new InvalidOperationException(
                $"CONTENT_MANIFEST is invalid ({problems.Count} problems): {head}");
        }
        return manifest;
    }

    static ReadOnlySpan<byte> StripBom(byte[] bytes)
    {
        var span = bytes.AsSpan();
        return span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF
            ? span[3..]
            : span;
    }

    public static void Save(string path, ContentManifest manifest)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
