using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// CHANNEL_MANIFEST / ShipReleaseEnvelope — content discovery SoT on GH/CDN.
/// r5fms is policy SoT only (releases.name = SDK_VERSION); never hosts this doc in v1.
/// </summary>
public sealed class ChannelManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "channel_manifest";

    /// <summary>WireVersion / SDK_VERSION — sole r5fms gate key.</summary>
    [JsonPropertyName("sdk_version")]
    public string SdkVersion { get; set; } = string.Empty;

    /// <summary>Alias for sdk_version when producers emit gate_name.</summary>
    [JsonPropertyName("gate_name")]
    public string? GateName { get; set; }

    [JsonPropertyName("marketing_tag")]
    public string MarketingTag { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    /// <summary>Optional CDN/GH base for relative asset URLs.</summary>
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("client")]
    public ChannelTrackTip? Client { get; set; }

    [JsonPropertyName("server")]
    public ChannelTrackTip? Server { get; set; }

    /// <summary>Live overlay tip (scripts + optional runtime binaries).</summary>
    [JsonPropertyName("platform")]
    public ChannelTrackTip? Platform { get; set; }

    /// <summary>HD textures (.opt.starpak). Opt-in; absent means the tip has none.</summary>
    [JsonPropertyName("hd")]
    public ChannelTrackTip? Hd { get; set; }

    /// <summary>Optional u32 audit list; never used as install file verify.</summary>
    [JsonPropertyName("script_checksums")]
    public List<uint>? ScriptChecksums { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Oldest shell version this channel supports. Additive: older launchers
    /// ignore it, which is precisely why it must be published before it is
    /// needed rather than when it is.
    /// </summary>
    [JsonPropertyName("min_launcher_version")]
    public string? MinLauncherVersion { get; set; }

    [JsonPropertyName("notes_url")]
    public string? NotesUrl { get; set; }

    public string EffectiveGateName =>
        !string.IsNullOrWhiteSpace(GateName) ? GateName! : SdkVersion;
}

/// <summary>Alias type name used in ship docs; same shape as <see cref="ChannelManifest"/>.</summary>
public sealed class ShipReleaseEnvelope
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "ship_release_envelope";

    [JsonPropertyName("sdk_version")]
    public string SdkVersion { get; set; } = string.Empty;

    [JsonPropertyName("gate_name")]
    public string? GateName { get; set; }

    [JsonPropertyName("marketing_tag")]
    public string MarketingTag { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("client")]
    public ChannelTrackTip? Client { get; set; }

    [JsonPropertyName("server")]
    public ChannelTrackTip? Server { get; set; }

    [JsonPropertyName("platform")]
    public ChannelTrackTip? Platform { get; set; }

    [JsonPropertyName("hd")]
    public ChannelTrackTip? Hd { get; set; }

    [JsonPropertyName("script_checksums")]
    public List<uint>? ScriptChecksums { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("notes_url")]
    public string? NotesUrl { get; set; }

    public ChannelManifest ToChannelManifest() => new()
    {
        Schema = Schema,
        Kind = string.IsNullOrWhiteSpace(Kind) ? "ship_release_envelope" : Kind,
        SdkVersion = SdkVersion,
        GateName = GateName,
        MarketingTag = MarketingTag,
        Channel = Channel,
        Prerelease = Prerelease,
        BaseUrl = BaseUrl,
        Client = Client,
        Server = Server,
        Platform = Platform,
        ScriptChecksums = ScriptChecksums,
        Notes = Notes,
        NotesUrl = NotesUrl,
    };
}

public sealed class ChannelTrackTip
{
    [JsonPropertyName("preset")]
    public string Preset { get; set; } = string.Empty;

    /// <summary>ContentCatalogSemver tip after the full base + patch chain.</summary>
    [JsonPropertyName("catalog_version")]
    public string CatalogVersion { get; set; } = string.Empty;

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Legacy single-tip share URL. When <see cref="Base"/> is set, prefer base/patches.
    /// </summary>
    [JsonPropertyName("share_manifest_url")]
    public string? ShareManifestUrl { get; set; }

    [JsonPropertyName("share_manifest_sha256")]
    public string? ShareManifestSha256 { get; set; }

    [JsonPropertyName("total_bytes")]
    public long? TotalBytes { get; set; }

    /// <summary>Local testing: absolute install root when assets are empty.</summary>
    [JsonPropertyName("local_install_hint")]
    public string? LocalInstallHint { get; set; }

    /// <summary>
    /// Full-track base share. Null with empty <see cref="Patches"/> is the legacy single-tip shape.
    /// </summary>
    [JsonPropertyName("base")]
    public ChannelTrackBase? Base { get; set; }

    /// <summary>Ordered, contiguous patches from base to tip. Empty/null means base-only or legacy tip.</summary>
    [JsonPropertyName("patches")]
    public List<ChannelPatchRef>? Patches { get; set; }

    /// <summary>Legacy tip-level asset list (single-tip manifests). Chain tips put volumes on base/patches.</summary>
    [JsonPropertyName("assets")]
    public List<ChannelAssetRef> Assets { get; set; } = new();

    /// <summary>
    /// CONTENT_MANIFEST for this tip. When set, the track installs from the
    /// content-addressed store and base/patches are ignored entirely. Additive:
    /// a launcher that does not know this field keeps using the share volumes.
    /// </summary>
    [JsonPropertyName("content_manifest_url")]
    public string? ContentManifestUrl { get; set; }

    [JsonPropertyName("content_manifest_sha256")]
    public string? ContentManifestSha256 { get; set; }

    /// <summary>Base for cas/ object URLs. Falls back to the manifest-level base_url.</summary>
    [JsonPropertyName("cas_base_url")]
    public string? CasBaseUrl { get; set; }

    /// <summary>True when this tip carries a base and/or patch chain (not legacy single-share only).</summary>
    [JsonIgnore]
    public bool HasPatchChain =>
        Base is not null || (Patches is { Count: > 0 });

    /// <summary>Selects the content-addressed install path over the volume path.</summary>
    [JsonIgnore]
    public bool UsesContentManifest =>
        !string.IsNullOrWhiteSpace(ContentManifestUrl);
}

/// <summary>Full-track base published once; share volumes cover the whole track at base catalog version.</summary>
public sealed class ChannelTrackBase
{
    [JsonPropertyName("catalog_version")]
    public string CatalogVersion { get; set; } = string.Empty;

    /// <summary>Content hash of the track at this base version when the producer emits it.</summary>
    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("share_manifest_url")]
    public string? ShareManifestUrl { get; set; }

    [JsonPropertyName("share_manifest_sha256")]
    public string? ShareManifestSha256 { get; set; }

    [JsonPropertyName("total_bytes")]
    public long? TotalBytes { get; set; }

    [JsonPropertyName("assets")]
    public List<ChannelAssetRef> Assets { get; set; } = new();
}

/// <summary>One contiguous delta in the track patch chain (from → to).</summary>
public sealed class ChannelPatchRef
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Content hash of the track after this patch when the producer emits it.</summary>
    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("patch_manifest_url")]
    public string? PatchManifestUrl { get; set; }

    [JsonPropertyName("patch_manifest_sha256")]
    public string? PatchManifestSha256 { get; set; }

    [JsonPropertyName("share_manifest_url")]
    public string? ShareManifestUrl { get; set; }

    [JsonPropertyName("share_manifest_sha256")]
    public string? ShareManifestSha256 { get; set; }

    [JsonPropertyName("total_bytes")]
    public long? TotalBytes { get; set; }

    [JsonPropertyName("assets")]
    public List<ChannelAssetRef> Assets { get; set; } = new();
}

public sealed class ChannelAssetRef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>share_manifest | share_volume | 7z_volume | patch_manifest | content_manifest | script_checksums | other</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "other";
}
