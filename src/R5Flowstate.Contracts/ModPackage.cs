using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

public sealed class ModPackage
{
    [JsonPropertyName("full_name")]
    public string FullName { get; init; } = string.Empty;

    [JsonPropertyName("owner")]
    public string Owner { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    [JsonPropertyName("is_deprecated")]
    public bool IsDeprecated { get; init; }

    [JsonPropertyName("has_nsfw_content")]
    public bool IsNsfw { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    [JsonPropertyName("versions")]
    public IReadOnlyList<ModPackageVersion> Versions { get; init; } = Array.Empty<ModPackageVersion>();
}

public sealed class ModPackageVersion
{
    [JsonPropertyName("version_number")]
    public string VersionNumber { get; init; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    [JsonPropertyName("file_size")]
    public long FileSize { get; init; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset Uploaded { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = string.Empty;
}

/// <summary>Ordered Thunderstore <c>Owner-Name-Version</c> pins (legacy profile).</summary>
public sealed class ModProfile
{
    public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();
}
