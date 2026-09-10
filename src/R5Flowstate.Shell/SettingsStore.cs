using System.IO;
using System.Linq;
using Microsoft.Win32;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>HKCU Software\R5Flowstate — single surface; no separate settings dialog.</summary>
public static class SettingsStore
{
    public const string RegistryPath = @"Software\R5Flowstate";

    /// <summary>Env override, checked before anything else. Dev machines set this
    /// instead of carrying a local path in source.</summary>
    public const string InstallPathEnvVar = "R5F_INSTALL_PATH";

    /// <summary>Files that mark a directory as a real game install.</summary>
    private static readonly string[] s_installMarkers =
    {
        "r5apex.exe", "r5apex_ds.exe", "client.dll",
    };

    /// <summary>
    /// Where a fresh launcher points when the registry has no choice yet.
    /// Shipped layout puts the launcher in the content root, so its own folder is
    /// the answer; a standalone copy falls back to a per-user directory it can
    /// install into. Never a machine-local path from source.
    /// </summary>
    public static string DefaultInstallPath => ResolveDefaultInstallPath();

    public static bool LooksLikeInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return s_installMarkers.Any(m => File.Exists(Path.Combine(path, m)));
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveDefaultInstallPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(InstallPathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var trimmed = fromEnv.Trim();
            if (!InstallPathPolicy.IsForbidden(trimmed, AppContext.BaseDirectory))
                return trimmed;
        }

        // Shipped next to the game: the launcher's own folder, or a parent if it
        // was placed in a subdirectory of the install.
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; dir is not null && depth < 3; depth++, dir = dir.Parent)
            {
                if (LooksLikeInstall(dir.FullName) &&
                    !InstallPathPolicy.IsForbidden(dir.FullName, AppContext.BaseDirectory))
                    return dir.FullName;
            }
        }
        catch
        {
            // fall through
        }

        return InstallPathPolicy.ResolveFreshDefault(AppContext.BaseDirectory);
    }

    public const string QuickPlayDefaultMap = "mp_rr_divided_moon_mu1";

    public static LauncherSettings Load()
    {
        var s = new LauncherSettings
        {
            InstallPath = DefaultInstallPath,
            ClientLaunchArguments = string.Empty,
            DediLaunchArguments = string.Empty,
            OfflineNoAuth = true,
            DediHostOnline = false,
            DevProfile = false,
            Cheats = true,
            DediPlaylist = "survival_dev",
            DediMap = QuickPlayDefaultMap,
            DediPort = 37015,
            DediPassword = string.Empty,
            DediPasswordEnabled = false,
            FilterMapsByPlaylist = false,
            ShowUnlistedMaps = false,
            SimpleMode = true,
            LastModePlaylist = string.Empty,
            EulaVersionAccepted = 0,
            ChannelUrl = string.Empty,
            InitialInstallAccepted = false,
            AutoApplyUpdates = false,
            ClientWidth = ResolutionCatalog.DefaultWidth,
            ClientHeight = ResolutionCatalog.DefaultHeight,
            ClientWindowMode = ResolutionCatalog.DefaultWindowMode,
        };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null)
                return s;

            if (key.GetValue("InstallPath") is string install && !string.IsNullOrWhiteSpace(install))
            {
                if (!InstallPathPolicy.IsForbidden(install, AppContext.BaseDirectory))
                    s.InstallPath = install;
            }

            if (key.GetValue("PreviousInstallPath") is string prev && !string.IsNullOrWhiteSpace(prev))
            {
                if (!InstallPathPolicy.IsForbidden(prev, AppContext.BaseDirectory))
                    s.PreviousInstallPath = prev;
            }

            if (key.GetValue("ClientLaunchArguments") is string clArgs)
                s.ClientLaunchArguments = clArgs;
            if (key.GetValue("DediLaunchArguments") is string svArgs)
                s.DediLaunchArguments = svArgs;

            if (string.IsNullOrEmpty(s.ClientLaunchArguments) &&
                string.IsNullOrEmpty(s.DediLaunchArguments) &&
                key.GetValue("LaunchArguments") is string legacy &&
                !string.IsNullOrEmpty(legacy))
            {
                s.ClientLaunchArguments = legacy;
                s.DediLaunchArguments = legacy;
            }

            s.OfflineNoAuth = ReadBool(key, "OfflineNoAuth", defaultValue: true);
            s.DediHostOnline = ReadBool(key, "DediHostOnline", defaultValue: false);
            s.DevProfile = ReadBool(key, "DevProfile", defaultValue: false);
            // Settings written before the split carry only DevProfile.
            s.ClientDevProfile = ReadBool(key, "ClientDevProfile", s.DevProfile);
            s.DediDevProfile = ReadBool(key, "DediDevProfile", s.DevProfile);
            s.Cheats = ReadBool(key, "Cheats", defaultValue: true);
            s.FilterMapsByPlaylist = ReadBool(key, "FilterMapsByPlaylist", defaultValue: false);
            s.ShowUnlistedMaps = ReadBool(key, "ShowUnlistedMaps", defaultValue: false);
            s.SimpleMode = ReadBool(key, "SimpleMode", defaultValue: true);

            if (key.GetValue("DediPlaylist") is string pl && !string.IsNullOrWhiteSpace(pl))
                s.DediPlaylist = pl;
            if (key.GetValue("DediMap") is string map && !string.IsNullOrWhiteSpace(map))
                s.DediMap = map;
            if (key.GetValue("DediPort") is int port && port > 0)
                s.DediPort = port;
            else if (key.GetValue("DediPort") is string portStr &&
                     int.TryParse(portStr, out var p) && p > 0)
                s.DediPort = p;
            if (key.GetValue("DediPassword") is string pw)
                s.DediPassword = pw ?? string.Empty;
            s.DediPasswordEnabled = ReadBool(key, "DediPasswordEnabled", defaultValue: false);

            if (key.GetValue("LastModePlaylist") is string lastMode)
                s.LastModePlaylist = lastMode ?? string.Empty;

            if (key.GetValue("ModeMaps") is string modeMapsRaw)
                s.ModeMaps = ParseModeMaps(modeMapsRaw);

            if (key.GetValue("FavoriteServers") is string favRaw && !string.IsNullOrWhiteSpace(favRaw))
            {
                foreach (var part in favRaw.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    s.FavoriteServers.Add(part.Trim());
            }

            s.EulaVersionAccepted = ReadInt(key, "EulaVersionAccepted", defaultValue: 0);
            if (key.GetValue("UiLanguage") is string uiLang)
                s.UiLanguage = uiLang ?? string.Empty;
            if (key.GetValue("EulaLanguage") is string eulaLang)
                s.EulaLanguage = eulaLang ?? string.Empty;
            s.ShowClientConsoleWindow = ReadBool(key, "ShowClientConsoleWindow", defaultValue: false);
            s.OpenConsoleOnLaunch = ReadBool(key, "OpenConsoleOnLaunch", defaultValue: false);
            s.UseDx12 = ReadBool(key, "UseDx12", defaultValue: false);
            s.DownloadLimitMbps = ReadInt(key, "DownloadLimitMbps", defaultValue: 0);
            s.DownloadConcurrency = ReadInt(key, "DownloadConcurrency", defaultValue: 0);
            s.HdTexturesAnnounced = ReadBool(key, "HdTexturesAnnounced", defaultValue: false);
            if (key.GetValue("NotesSeenStamp") is string notesSeen)
                s.NotesSeenStamp = notesSeen ?? string.Empty;
            if (key.GetValue("BlogSeenStamp") is string blogSeen)
                s.BlogSeenStamp = blogSeen ?? string.Empty;

            if (key.GetValue("ConsoleHistoryServer") is string[] histServer)
                s.ConsoleHistoryServer.AddRange(histServer.Where(l => !string.IsNullOrWhiteSpace(l)));
            if (key.GetValue("ConsoleHistoryClient") is string[] histClient)
                s.ConsoleHistoryClient.AddRange(histClient.Where(l => !string.IsNullOrWhiteSpace(l)));

            if (key.GetValue("ChannelUrl") is string chUrl)
                s.ChannelUrl = chUrl ?? string.Empty;
            s.InitialInstallAccepted = ReadBool(key, "InitialInstallAccepted", defaultValue: false);
            s.AutoApplyUpdates = ReadBool(key, "AutoApplyUpdates", defaultValue: false);
            // A resolution is always sent, so an absent or nonsense saved value
            // resolves to the default rather than to "unset".
            s.ClientWidth = ResolutionCatalog.ClampDimension(
                ReadInt(key, "ClientWidth", ResolutionCatalog.DefaultWidth), ResolutionCatalog.DefaultWidth);
            s.ClientHeight = ResolutionCatalog.ClampDimension(
                ReadInt(key, "ClientHeight", ResolutionCatalog.DefaultHeight), ResolutionCatalog.DefaultHeight);
            s.ClientWindowMode = ResolutionCatalog.ClampWindowMode(
                ReadInt(key, "ClientWindowMode", (int)ResolutionCatalog.DefaultWindowMode));
        }
        catch
        {
            // defaults remain
        }

        // Packaged Setup must not inherit an operator master tree (s21-full).
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InstallPathEnvVar))
            && IsPackagedShell()
            && IsOperatorMasterTree(s.InstallPath))
        {
            s.InstallPath = InstallPathPolicy.ResolveFreshDefault();
            s.InitialInstallAccepted = false;
        }

        return s;
    }

    public static void Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath)
            ?? throw new InvalidOperationException("Could not open HKCU\\" + RegistryPath);

        key.SetValue("InstallPath", settings.InstallPath ?? DefaultInstallPath);
        key.SetValue("PreviousInstallPath", settings.PreviousInstallPath ?? string.Empty);
        key.SetValue("ClientLaunchArguments", settings.ClientLaunchArguments ?? string.Empty);
        key.SetValue("DediLaunchArguments", settings.DediLaunchArguments ?? string.Empty);
        key.SetValue("OfflineNoAuth", settings.OfflineNoAuth ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DediHostOnline", settings.DediHostOnline ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DevProfile", settings.DevProfile ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ClientDevProfile", settings.ClientDevProfile ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DediDevProfile", settings.DediDevProfile ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("Cheats", settings.Cheats ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("FilterMapsByPlaylist", settings.FilterMapsByPlaylist ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ShowUnlistedMaps", settings.ShowUnlistedMaps ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("SimpleMode", settings.SimpleMode ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DediPlaylist", settings.DediPlaylist ?? string.Empty);
        key.SetValue("DediMap", settings.DediMap ?? string.Empty);
        key.SetValue("DediPort", settings.DediPort > 0 ? settings.DediPort : 37015, RegistryValueKind.DWord);
        key.SetValue("DediPassword", settings.DediPassword ?? string.Empty);
        key.SetValue("DediPasswordEnabled", settings.DediPasswordEnabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("LastModePlaylist", settings.LastModePlaylist ?? string.Empty);
        key.SetValue("ModeMaps", FormatModeMaps(settings.ModeMaps));
        key.SetValue("FavoriteServers", string.Join(";", settings.FavoriteServers));
        key.SetValue("EulaVersionAccepted", settings.EulaVersionAccepted, RegistryValueKind.DWord);
        var lang = NoticeLanguages.ForUi(
            !string.IsNullOrWhiteSpace(settings.UiLanguage)
                ? settings.UiLanguage
                : settings.EulaLanguage);
        settings.UiLanguage = lang;
        settings.EulaLanguage = lang;
        key.SetValue("UiLanguage", lang);
        key.SetValue("EulaLanguage", lang);
        key.SetValue("ShowClientConsoleWindow", settings.ShowClientConsoleWindow ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("OpenConsoleOnLaunch", settings.OpenConsoleOnLaunch ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("UseDx12", settings.UseDx12 ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("DownloadLimitMbps", Math.Max(0, settings.DownloadLimitMbps), RegistryValueKind.DWord);
        key.SetValue("DownloadConcurrency", Math.Max(0, settings.DownloadConcurrency), RegistryValueKind.DWord);
        key.SetValue("HdTexturesAnnounced", settings.HdTexturesAnnounced ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("NotesSeenStamp", settings.NotesSeenStamp ?? string.Empty);
        key.SetValue("BlogSeenStamp", settings.BlogSeenStamp ?? string.Empty);
        key.SetValue("ConsoleHistoryServer", settings.ConsoleHistoryServer.ToArray(), RegistryValueKind.MultiString);
        key.SetValue("ConsoleHistoryClient", settings.ConsoleHistoryClient.ToArray(), RegistryValueKind.MultiString);
        key.SetValue("ChannelUrl", settings.ChannelUrl ?? string.Empty);
        key.SetValue("InitialInstallAccepted", settings.InitialInstallAccepted ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AutoApplyUpdates", settings.AutoApplyUpdates ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ClientWidth",
            ResolutionCatalog.ClampDimension(settings.ClientWidth, ResolutionCatalog.DefaultWidth),
            RegistryValueKind.DWord);
        key.SetValue("ClientHeight",
            ResolutionCatalog.ClampDimension(settings.ClientHeight, ResolutionCatalog.DefaultHeight),
            RegistryValueKind.DWord);
        key.SetValue("ClientWindowMode",
            (int)ResolutionCatalog.ClampWindowMode((int)settings.ClientWindowMode),
            RegistryValueKind.DWord);
        key.Flush();
    }

    private static bool ReadBool(RegistryKey key, string name, bool defaultValue)
    {
        if (key.GetValue(name) is int flag)
            return flag != 0;
        if (key.GetValue(name) is string flagStr && int.TryParse(flagStr, out var parsed))
            return parsed != 0;
        return defaultValue;
    }

    static bool IsPackagedShell()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 3 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Update.exe")))
                    return true;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }

    static bool IsOperatorMasterTree(string path)
    {
        if (!LooksLikeInstall(path))
            return false;
        try
        {
            return !File.Exists(Path.Combine(path, ProductConstants.InstallStateFileName));
        }
        catch
        {
            return false;
        }
    }

    private static int ReadInt(RegistryKey key, string name, int defaultValue)
    {
        if (key.GetValue(name) is int n)
            return n;
        if (key.GetValue(name) is string s && int.TryParse(s, out var parsed))
            return parsed;
        return defaultValue;
    }

    /// <summary>Parse id=map;id=map;... into a case-insensitive map.</summary>
    public static Dictionary<string, string> ParseModeMaps(string? raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return dict;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq >= part.Length - 1)
                continue;
            var id = part[..eq].Trim();
            var map = part[(eq + 1)..].Trim();
            if (id.Length > 0 && map.Length > 0)
                dict[id] = map;
        }

        return dict;
    }

    /// <summary>Serialize mode map memory as id=map;id=map;...</summary>
    public static string FormatModeMaps(IReadOnlyDictionary<string, string>? maps)
    {
        if (maps is null || maps.Count == 0)
            return string.Empty;
        return string.Join(";", maps
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{kv.Key.Trim()}={kv.Value.Trim()}"));
    }
}

public sealed class LauncherSettings
{
    public string InstallPath { get; set; } = SettingsStore.DefaultInstallPath;

    /// <summary>Last folder that held a real install before the path was changed.
    /// Startup falls back to it when the current path has no game, so a mistaken
    /// Browse or Reset cannot strand the launcher on an empty folder.</summary>
    public string PreviousInstallPath { get; set; } = string.Empty;
    public string ClientLaunchArguments { get; set; } = string.Empty;
    public string DediLaunchArguments { get; set; } = string.Empty;
    public bool OfflineNoAuth { get; set; }

    /// <summary>Advanced: publish the dedicated server to Spire (+spire_host_visibility 2).</summary>
    public bool DediHostOnline { get; set; }

    /// <summary>Simple mode's Developer switch. Sets both per-side flags.</summary>
    public bool DevProfile { get; set; }
    public bool Cheats { get; set; } = true;
    public string DediPlaylist { get; set; } = "survival_dev";
    public string DediMap { get; set; } = SettingsStore.QuickPlayDefaultMap;
    public int DediPort { get; set; } = 37015;
    public string DediPassword { get; set; } = string.Empty;
    public bool DediPasswordEnabled { get; set; }
    public bool FilterMapsByPlaylist { get; set; }

    /// <summary>
    /// Simple picker also offers on-disk mp_rr_* stems that are not in
    /// r5f_map_names.txt. Off by default; the ship allowlist stays the player list.
    /// platform/r5f_wip_maps.txt still adds named extras with this off.
    /// </summary>
    public bool ShowUnlistedMaps { get; set; }

    /// <summary>True = Simple player shell (default for new installs).</summary>
    public bool SimpleMode { get; set; } = true;

    /// <summary>Last selected mode card playlist id.</summary>
    public string LastModePlaylist { get; set; } = string.Empty;

    /// <summary>Per-mode remembered map stems (playlist id -> map).</summary>
    public Dictionary<string, string> ModeMaps { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Favourite servers as "ip:port"; pinned to the top of the browser.</summary>
    public HashSet<string> FavoriteServers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local stamp of the last accepted EULA version. Never sent to the master.</summary>
    public int EulaVersionAccepted { get; set; }

    /// <summary>Launcher UI language (game locale). Empty = detect from OS.</summary>
    public string UiLanguage { get; set; } = string.Empty;

    /// <summary>Last EULA read language (r5fms locale). Kept in lockstep with UiLanguage.</summary>
    public string EulaLanguage { get; set; } = string.Empty;

    /// <summary>Simple local play: leave the client AllocConsole window visible.</summary>
    public bool ShowClientConsoleWindow { get; set; }

    /// <summary>
    /// Play / Join: host logs in the launcher Console tab.
    /// Advanced Launch Client / Launch Dedi always keep their own windows.
    /// </summary>
    public bool OpenConsoleOnLaunch { get; set; }

    /// <summary>The one-time "HD textures are available" notice has been shown.</summary>
    public bool HdTexturesAnnounced { get; set; }

    /// <summary>Run r5apex_dx12.exe.</summary>
    public bool UseDx12 { get; set; }

    /// <summary>Game download cap in Mbps. 0 = unlimited.</summary>
    public int DownloadLimitMbps { get; set; }

    /// <summary>0 = automatic.</summary>
    public int DownloadConcurrency { get; set; }

    /// <summary>Client -width. Always sent; never unset.</summary>
    public int ClientWidth { get; set; } = ResolutionCatalog.DefaultWidth;

    /// <summary>Client -height. Always sent; never unset.</summary>
    public int ClientHeight { get; set; } = ResolutionCatalog.DefaultHeight;

    /// <summary>Windowed / borderless / fullscreen. Always sent; never unset.</summary>
    public ClientWindowMode ClientWindowMode { get; set; } = ResolutionCatalog.DefaultWindowMode;

    /// <summary>-dev -devsdk on the client. Simple mode drives both sides at once.</summary>
    public bool ClientDevProfile { get; set; }

    /// <summary>-dev -devsdk on the dedi. Independent of the client flag.</summary>
    public bool DediDevProfile { get; set; }

    /// <summary>Last opened notes fingerprint. Empty = never opened.</summary>
    public string NotesSeenStamp { get; set; } = string.Empty;

    /// <summary>Last opened blog fingerprint. Empty = never opened.</summary>
    public string BlogSeenStamp { get; set; } = string.Empty;

    /// <summary>Console tab command history, oldest first.</summary>
    public List<string> ConsoleHistoryServer { get; set; } = new();

    public List<string> ConsoleHistoryClient { get; set; } = new();

    public string ChannelUrl { get; set; } = string.Empty;

    public bool InitialInstallAccepted { get; set; }

    /// <summary>Force WPF software rendering (for machines with broken GPU drivers).</summary>
    public bool ForceSoftwareRender { get; set; }

    public bool AutoApplyUpdates { get; set; }
}
