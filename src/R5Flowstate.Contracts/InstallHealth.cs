namespace R5Flowstate.Contracts;

/// <summary>Per-track or overall content health vs CHANNEL tip + on-disk files.</summary>
public enum InstallHealthStatus
{
    /// <summary>No INSTALL_STATE and no installed track evidence.</summary>
    Missing = 0,

    /// <summary>Install started or partial; incomplete=true or ready flags off.</summary>
    Incomplete = 1,

    /// <summary>Tip matches INSTALL_STATE and files verify (or local tip without assets).</summary>
    Ready = 2,

    /// <summary>CHANNEL tip catalog_version / content_hash ahead of INSTALL_STATE.</summary>
    UpdateAvailable = 3,

    /// <summary>State claims ready but files missing/size mismatch (or verify failed).</summary>
    Corrupted = 4,

    /// <summary>
    /// Files are present; INSTALL_FILES is missing, schema-1, or the wrong volume.
    /// PLAY runs a full hash before it will start the game.
    /// </summary>
    Unverified = 5,
}

public sealed class TrackHealth
{
    public string Preset { get; set; } = string.Empty;
    public InstallHealthStatus Status { get; set; } = InstallHealthStatus.Missing;
    public string? Reason { get; set; }
    public string? InstalledCatalogVersion { get; set; }
    public string? TipCatalogVersion { get; set; }
    public string? InstalledContentHash { get; set; }
    public string? TipContentHash { get; set; }
    public int MissingFileCount { get; set; }
    public int SizeMismatchCount { get; set; }
    public List<string> SampleIssues { get; set; } = new();
}

public sealed class InstallHealthReport
{
    public InstallHealthStatus Overall { get; set; } = InstallHealthStatus.Missing;
    public string InstallPath { get; set; } = string.Empty;
    public bool HasInstallState { get; set; }
    public bool Enforced { get; set; }
    public TrackHealth? Client { get; set; }
    public TrackHealth? Server { get; set; }
    public TrackHealth? Platform { get; set; }

    /// <summary>Null unless the player opted into HD; opting out is not a fault.</summary>
    public TrackHealth? Hd { get; set; }

    /// <summary>
    /// Overlay hashes that differ from the pack. Not corruption: PLAY still
    /// works, and Restore official is how the player puts the pack back.
    /// </summary>
    public OverlayEditReport? Overlay { get; set; }

    public List<string> Reasons { get; set; } = new();

    public bool HasOverlayEdits => Overlay?.HasEdits == true;

    public int OverlayEditCount => Overlay?.Total ?? 0;

    public int BrokenFileCount
    {
        get
        {
            static int N(TrackHealth? t) =>
                t is null ? 0 : t.MissingFileCount + t.SizeMismatchCount;
            return N(Client) + N(Server) + N(Platform) + N(Hd);
        }
    }

    public bool BlocksPlay =>
        Overall is InstallHealthStatus.Missing
            or InstallHealthStatus.Incomplete
            or InstallHealthStatus.UpdateAvailable
            or InstallHealthStatus.Corrupted
            or InstallHealthStatus.Unverified;

    public bool NeedsRepair => Overall == InstallHealthStatus.Corrupted;

    public bool NeedsVerify => Overall == InstallHealthStatus.Unverified;

    public bool NeedsUpdate => Overall == InstallHealthStatus.UpdateAvailable;

    public bool IsReady => Overall == InstallHealthStatus.Ready;

    public string Summary
    {
        get
        {
            if (Reasons.Count == 0)
                return Overall.ToString();
            return Overall + ": " + string.Join("; ", Reasons.Take(4));
        }
    }
}
