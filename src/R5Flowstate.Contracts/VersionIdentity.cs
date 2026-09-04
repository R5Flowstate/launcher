namespace R5Flowstate.Contracts;

/// <summary>
/// Four non-interchangeable identities. Never collapse catalog/build_stamp into wire.
/// </summary>
public static class VersionIdentity
{
    public const string WireVersionName = "WireVersion";
    public const string ContentCatalogSemverName = "ContentCatalogSemver";
    public const string BuildStampFingerprintName = "BuildStampFingerprint";
    public const string MarketingTagName = "MarketingTag";

    /// <summary>
    /// Live compiled sticky gate from unify basetypes.h (refresh when wire bumps).
    /// </summary>
    public const string CompiledSdkVersion = "R5FlowstateSDK001";

    /// <summary>
    /// True when the string looks like a sticky SDK_VERSION gate name
    /// (e.g. R5FlowstateSDK001), not a catalog semver or R5F build stamp.
    /// </summary>
    public static bool LooksLikeWireVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d+\.\d+"))
            return false;

        if (value.StartsWith("R5pc_", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("_R5F", StringComparison.OrdinalIgnoreCase))
            return false;

        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^v?\d+\.\d+"))
            return false;

        return value.Contains("SDK", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("R5Flowstate", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureWireNotCatalog(string wireVersion, string? catalogVersion)
    {
        if (string.IsNullOrWhiteSpace(wireVersion))
            throw new ArgumentException("WireVersion (SDK_VERSION) must not be empty.", nameof(wireVersion));

        if (!string.IsNullOrWhiteSpace(catalogVersion) &&
            string.Equals(wireVersion, catalogVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "WireVersion must not equal ContentCatalogSemver. " +
                $"Got both as '{wireVersion}'.");
        }
    }

    public static VersionBundle FromManifest(ChannelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var wire = manifest.EffectiveGateName;
        EnsureWireNotCatalog(wire, manifest.Client?.CatalogVersion);
        EnsureWireNotCatalog(wire, manifest.Server?.CatalogVersion);
        EnsureWireNotCatalog(wire, manifest.Platform?.CatalogVersion);

        if (!string.IsNullOrWhiteSpace(manifest.GateName) &&
            !string.IsNullOrWhiteSpace(manifest.SdkVersion) &&
            !string.Equals(manifest.GateName, manifest.SdkVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"gate_name '{manifest.GateName}' must equal sdk_version '{manifest.SdkVersion}'.");
        }

        return new VersionBundle(
            WireVersion: wire,
            ClientCatalogSemver: manifest.Client?.CatalogVersion,
            ServerCatalogSemver: manifest.Server?.CatalogVersion,
            MarketingTag: manifest.MarketingTag,
            Channel: manifest.Channel,
            Prerelease: manifest.Prerelease);
    }

    /// <summary>
    /// Read optional ship stamp <c>r5f_sdk_version.txt</c> under an install root.
    /// Returns null when missing or blank. Does not invent a version.
    /// </summary>
    public static string? TryReadSdkVersionStamp(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return null;

        var path = Path.Combine(installRoot, ProductConstants.SdkVersionStampFileName);
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Prefer stamp when present; otherwise fall back to expected/compiled wire string.
    /// </summary>
    public static string ResolveExpectedWireVersion(string installRoot, string? fallbackWireVersion = null)
    {
        var stamp = TryReadSdkVersionStamp(installRoot);
        if (!string.IsNullOrWhiteSpace(stamp))
            return stamp!;

        if (!string.IsNullOrWhiteSpace(fallbackWireVersion))
            return fallbackWireVersion!;

        return CompiledSdkVersion;
    }

    public static bool StampMatches(string installRoot, string expectedWireVersion)
    {
        var stamp = TryReadSdkVersionStamp(installRoot);
        if (stamp is null)
            return true;

        return string.Equals(stamp, expectedWireVersion, StringComparison.Ordinal);
    }
}

public readonly record struct VersionBundle(
    string WireVersion,
    string? ClientCatalogSemver,
    string? ServerCatalogSemver,
    string MarketingTag,
    string Channel,
    bool Prerelease);
