namespace R5Flowstate.Contracts;

/// <summary>
/// Fat vs live-overlay path ownership. Must match the build manager's allowlist.
/// </summary>
public static class OverlayPaths
{
    static readonly string[] FatOwnedExact =
    {
        "platform/net_prophuff.dat",
    };

    static readonly string[] FatOwnedPrefixes =
    {
        "platform/settings/",
    };

    // HD textures. They sit under paks/, a root the client track owns, but the
    // client manifest must not list them or every player pays 86 GiB. Their own
    // class keeps the client track from deleting them as leftovers.
    const string OptOwnedSuffix = ".opt.starpak";

    public static readonly string[] OverlayOwnedExact =
    {
        "platform/playlists_r5_patch.txt",
        "platform/r5f_map_names.txt",
        "playlists_r5_patch.txt",
        "r2/playlists_r5.txt",
    };

    public static readonly string[] OverlayOwnedPrefixes =
    {
        "platform/scripts/",
        "platform/cfg/",
        "platform/localization/",
        "platform/datatable/",
        "platform/resource/",
    };

    static readonly string[] OverlayOptionalExact =
    {
        "r5apex.exe",
        "r5apex_ds.exe",
        "r5apexdata.bin",
        "client.dll",
        "server.dll",
        "loader.dll",
        "r5f_sdk_version.txt",
    };

    // Disk maps. The client track owns platform/ (scripts live there) and
    // classifies this prefix as Fat, so leftover-sweep would delete every
    // custom map a player drops. Not overlay-owned: that class is packed
    // into the platform tip and leftover-wiped on WriteOfficial.
    static readonly string[] PlayerMapPrefixes =
    {
        "platform/maps/",
        "maps/navmesh/",
    };

    public enum Class
    {
        Fat,
        FatOwned,
        OverlayOwned,
        OverlayOptional,
        OptOwned,
    }

    public static string Norm(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
            return string.Empty;
        var r = rel.Replace('\\', '/').Trim().TrimStart('/');
        while (r.Contains("//", StringComparison.Ordinal))
            r = r.Replace("//", "/", StringComparison.Ordinal);
        return r;
    }

    public static Class Classify(string? rel)
    {
        var r = Norm(rel).ToLowerInvariant();
        if (r.Length == 0)
            return Class.Fat;
        if (r.EndsWith(OptOwnedSuffix, StringComparison.Ordinal))
            return Class.OptOwned;
        foreach (var e in FatOwnedExact)
        {
            if (r == e)
                return Class.FatOwned;
        }
        foreach (var p in FatOwnedPrefixes)
        {
            if (r.StartsWith(p, StringComparison.Ordinal) || r == p.TrimEnd('/'))
                return Class.FatOwned;
        }
        foreach (var e in OverlayOwnedExact)
        {
            if (r == e)
                return Class.OverlayOwned;
        }
        foreach (var p in OverlayOwnedPrefixes)
        {
            if (r.StartsWith(p, StringComparison.Ordinal) || r == p.TrimEnd('/'))
                return Class.OverlayOwned;
        }
        foreach (var e in OverlayOptionalExact)
        {
            if (r == e)
                return Class.OverlayOptional;
        }
        return Class.Fat;
    }

    public static bool IsFatIdentity(string? rel)
    {
        var c = Classify(rel);
        return c is Class.Fat or Class.FatOwned;
    }

    public static bool SkipFatVerify(string? rel)
    {
        var c = Classify(rel);
        return c is Class.OverlayOwned or Class.OverlayOptional or Class.OptOwned;
    }

    /// <summary>
    /// Official packed map or a player disk map. common.rpak is not this.
    /// </summary>
    public static bool IsMapPayload(string? rel)
    {
        var r = Norm(rel).ToLowerInvariant();
        if (r.Length == 0)
            return false;
        if (r.EndsWith(OptOwnedSuffix, StringComparison.Ordinal))
            return false;
        if (IsPlayerMap(r))
            return true;
        return IsOfficialMapName(FileName(r));
    }

    /// <summary>
    /// A manifest-owned mp_rr_* pak or map VPK. Disk maps are not this.
    /// </summary>
    public static bool IsOfficialMapPayload(string? rel)
    {
        var r = Norm(rel).ToLowerInvariant();
        if (r.Length == 0 || r.EndsWith(OptOwnedSuffix, StringComparison.Ordinal))
            return false;
        if (IsPlayerMap(r))
            return false;
        return IsOfficialMapName(FileName(r));
    }

    /// <summary>
    /// Size drift here is a player edit, not a truncated official file.
    /// Official maps are not exempt: a short VPK is damage.
    /// </summary>
    public static bool IgnoreSizeMismatch(string? rel)
    {
        var c = Classify(rel);
        return c is Class.OverlayOwned or Class.OverlayOptional;
    }

    /// <summary>
    /// Map stem used to pair a leftover disk BSP with the official pak/VPK.
    /// `paks/Win64/mp_rr_district.rpak` and `vpk/server_mp_rr_district.bsp.pak000_dir.vpk`
    /// both yield `mp_rr_district`.
    /// </summary>
    public static string? OfficialMapStem(string? rel)
    {
        var name = FileName(Norm(rel).ToLowerInvariant());
        if (name.Length == 0)
            return null;
        var at = name.IndexOf("mp_rr_", StringComparison.Ordinal);
        if (at < 0)
            return null;
        var stem = name[at..];
        var bsp = stem.IndexOf(".bsp", StringComparison.Ordinal);
        if (bsp >= 0)
            return stem[..bsp];
        var dot = stem.IndexOf('.');
        return dot >= 0 ? stem[..dot] : stem;
    }

    public static HashSet<string> OfficialMapStems(IEnumerable<string?> paths)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!IsOfficialMapPayload(path))
                continue;
            var stem = OfficialMapStem(path);
            if (!string.IsNullOrEmpty(stem))
                stems.Add(stem);
        }
        return stems;
    }

    /// <summary>
    /// Loose disk map that the engine will open ahead of the official VPK.
    /// Navmesh under maps/navmesh/ is not this.
    /// </summary>
    public static bool IsShadowLeftover(string? rel, ISet<string> officialStems)
    {
        ArgumentNullException.ThrowIfNull(officialStems);
        var r = Norm(rel).ToLowerInvariant();
        if (r.Length == 0 || IsNavmesh(r))
            return false;
        if (!r.StartsWith("platform/maps/", StringComparison.Ordinal) &&
            !r.StartsWith("maps/", StringComparison.Ordinal))
            return false;
        var stem = OfficialMapStem(r);
        return stem is not null && officialStems.Contains(stem);
    }

    public static bool IsOptOwned(string? rel) =>
        Classify(rel) == Class.OptOwned;

    public static bool IsOverlayOwned(string? rel) =>
        Classify(rel) == Class.OverlayOwned;

    public static bool IsOverlayOptional(string? rel) =>
        Classify(rel) == Class.OverlayOptional;

    public static bool IsPlayerMap(string? rel)
    {
        var r = Norm(rel).ToLowerInvariant();
        if (r.Length == 0)
            return false;
        foreach (var p in PlayerMapPrefixes)
        {
            if (r.StartsWith(p, StringComparison.Ordinal) || r == p.TrimEnd('/'))
                return true;
        }
        return false;
    }

    public static bool IsNavmesh(string? rel)
    {
        var r = Norm(rel).ToLowerInvariant();
        return r.StartsWith("maps/navmesh/", StringComparison.Ordinal)
               || r == "maps/navmesh";
    }

    static string FileName(string r)
    {
        var slash = r.LastIndexOf('/');
        return slash >= 0 ? r[(slash + 1)..] : r;
    }

    static bool IsOfficialMapName(string name) =>
        name.StartsWith("mp_rr_", StringComparison.Ordinal)
        || name.Contains("_mp_rr_", StringComparison.Ordinal);
}
