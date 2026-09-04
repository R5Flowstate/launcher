namespace R5Flowstate.Contracts;

/// <summary>Brand and path constants for the thin player shell.</summary>
public static class ProductConstants
{
    public const string ProductName = "R5Flowstate";
    public const string SetupExeName = "R5FlowstateSetup.exe";
    public const string ShellExeName = "R5Flowstate.exe";

    /// <summary>Optional install-root stamp written by ship/share overlay.</summary>
    public const string SdkVersionStampFileName = "r5f_sdk_version.txt";

    public const string ChannelManifestFileName = "CHANNEL_MANIFEST.json";
    public const string NotesFileName = "NOTES.json";

    /// <summary>Bundled launcher changelog; the CDN copy lives at launcher-feed/NOTES.json.</summary>
    public const string LauncherNotesFileName = "LAUNCHER_NOTES.json";
    public const string InstallStateFileName = "INSTALL_STATE.json";

    /// <summary>Per-file state mirror written next to INSTALL_STATE.</summary>
    public const string InstallFilesFileName = "INSTALL_FILES.json";
    public const string ShareManifestFileName = "SHARE_MANIFEST.json";
    public const string PatchManifestFileName = "PATCH_MANIFEST.json";

    /// <summary>InstallPath-relative content cache root (parts staged under .r5f/cache/).</summary>
    public const string ContentCacheDirName = ".r5f";

    /// <summary>InstallPath-relative player mods root. Repair must never delete this.</summary>
    public const string ModsDirName = "mods";

    /// <summary>
    /// Live Thunderstore catalog (browse, profiles, join-install). Off until the
    /// r5flowstate community exists. Local zip install stays.
    /// </summary>
    public const bool ThunderstoreEnabled = false;

    public const string RegistryKeyPath = @"Software\R5Flowstate";

    /// <summary>Public r5fms play host. Shell lists via POST /spire/hosts only.</summary>
    public const string DefaultMasterServerUrl = "https://play.r5flowstate.org";

    public const string DefaultChannelUrl =
        "https://cdn.r5flowstate.org/channel/CHANNEL_MANIFEST.json";

    public const string DefaultLauncherFeedUrl =
        "https://cdn.r5flowstate.org/launcher-feed/";

    public const string SkipSelfUpdateEnvVar = "R5F_SKIP_SELF_UPDATE";

    /// <summary>
    /// Per-logon-session mutex leaf. The shell prefixes Local\. A second
    /// start must raise the live window -- two shells both adopt the same
    /// client/dedi images under InstallPath.
    /// </summary>
    public const string SingleInstanceMutexName = "R5Flowstate.Launcher";

    public const string WebsiteUrl = "https://r5flowstate.org";

    /// <summary>Public Setup share. Always the latest wrapper; r5fms 302s when ops downloads are on.</summary>
    public const string DefaultSetupUrl = "https://r5flowstate.org/launcher";
    public const string DediPackageUrl = "https://r5flowstate.org/dedi";
    public const string ToolsReposUrl =
        "https://github.com/orgs/R5Flowstate/repositories";
    public const string GitHubUrl = "https://github.com/CafeFPS/r5f-launcher";
    public const string DiscordUrl = "https://discord.gg/REQmKk35Kx";
}
