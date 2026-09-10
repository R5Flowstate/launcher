namespace R5Flowstate.Contracts;

/// <summary>Which content tracks the installer should fetch into InstallPath.</summary>
public enum InstallMode
{
    /// <summary>Client then server; both when present on channel. Client tip required.</summary>
    Full = 0,

    /// <summary>Client track only.</summary>
    ClientOnly = 1,

    /// <summary>Server track into an existing InstallPath (secondary download).</summary>
    ServerSecondary = 2,

    /// <summary>Server track only (headless / dedi operators).</summary>
    DedicatedOnly = 3,
}
