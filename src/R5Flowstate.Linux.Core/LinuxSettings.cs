using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Linux port of SettingsStore (HKCU Software\R5Flowstate) as JSON.
/// Path: ~/.config/r5flowstate/settings.json. Keeps R5F_INSTALL_PATH env override.
/// </summary>
public sealed class LinuxSettings
{
    public string InstallPath { get; set; } = "";
    public string PreviousInstallPath { get; set; } = "";
    public string ProtonDir { get; set; } = "";
    public string PrefixPath { get; set; } = "";

    /// <summary>
    /// <b>Legacy, read-only.</b> The EA App's runtime, as an old settings file spells
    /// it: <c>"wine"</c> meant the system Wine in a prefix of its own, <c>"proton"</c>
    /// the game's prefix.
    ///
    /// <para>There is one runtime now — Proton, in the game's prefix — so this is
    /// never read and never written. It exists so a file written by the Wine-era
    /// launcher still <em>loads</em>: without the property, a hand-copied settings
    /// file would fail deserialization, and dropping an unknown key is a much worse
    /// answer than ignoring a known one. <see cref="Save"/> removes both keys, so the
    /// next save is a settings file of the current shape.</para>
    /// </summary>
    public string? EaRuntime { get; set; }

    /// <summary><b>Legacy, read-only.</b> The EA App's own prefix path under the
    /// split runtime. Same story as <see cref="EaRuntime"/>: parsed when present,
    /// never used and never written — the EA App lives in the game's prefix.</summary>
    public string? EaPrefixPath { get; set; }

    /// <summary>True when this object came from a file that named the split runtime,
    /// so the caller can say once what happened to it.
    ///
    /// <para>Computed from <see cref="EaRuntime"/> rather than assigned during
    /// <see cref="Load"/>, which is what it used to be. A flag set by one code path
    /// and read by others is a flag that can be deserialized without ever being set —
    /// which is exactly what the suite's fixtures do, and how the migration came out
    /// untested in the first place.</para></summary>
    [JsonIgnore]
    public bool MigratedFromSplitRuntime => IsSplitRuntimeSpelling(EaRuntime);

    /// <summary>The one line that explains the migration, or null when there was
    /// nothing to migrate. Here rather than in the caller so the wording and the
    /// condition cannot drift apart.</summary>
    [JsonIgnore]
    public string? MigrationNotice => MigratedFromSplitRuntime
        ? "This settings file was written for the Wine runtime. The EA App's own Wine prefix is "
          + "gone: the EA App and the game now share the game's Proton prefix, which is what makes "
          + "in-game auth work. The EaRuntime and EaPrefixPath keys are dropped on the next save."
        : null;

    /// <summary>Bring the EA App up before an online launch, and wait for its
    /// channel. The game cannot prove its account to a master server without it,
    /// so the default is on; off means the player starts EA themselves and the
    /// launcher only refuses the join when it is not there.</summary>
    public bool StartEaWithGame { get; set; } = true;

    /// <summary>Whether the install path may fetch Proton-GE on its own when
    /// Steam ships no Proton build. On by default — the fallback the launcher has
    /// always had — but visible in Settings and switchable off, because it is a
    /// ~500 MB download nobody asked for.</summary>
    public bool AutoDownloadProtonGe { get; set; } = true;

    public string ClientLaunchArguments { get; set; } = "";
    public string DediLaunchArguments { get; set; } = "";
    public bool OfflineNoAuth { get; set; }
    public bool DediHostOnline { get; set; }
    public bool DevProfile { get; set; }
    public bool ClientDevProfile { get; set; }
    public bool DediDevProfile { get; set; }
    public bool Cheats { get; set; } = true;
    public string DediPlaylist { get; set; } = "survival_dev";
    public string DediMap { get; set; } = DefaultMap;

    /// <summary>The map the launcher opens on, and the stand-in the rail uses
    /// before an install exists. Windows parity: SettingsStore's DediMap default.</summary>
    public const string DefaultMap = "mp_rr_divided_moon_mu1";
    public int DediPort { get; set; } = 37015;
    public string DediPassword { get; set; } = "";
    public bool DediPasswordEnabled { get; set; }
    public bool ShowUnlistedMaps { get; set; }
    public bool JoinWithoutDev { get; set; } = true;

    /// <summary>Family id the Simple rail was left on. Windows parity:
    /// SettingsStore.LastModePlaylist, so the launcher reopens on your mode.</summary>
    public string LastModePlaylist { get; set; } = "";

    /// <summary>Which map each family was last played on. Windows parity:
    /// SettingsStore.ModeMaps, where it is one "id=stem;id=stem" string; JSON
    /// carries the map directly, and the lookups below keep the same
    /// case-insensitive behaviour the registry form had.</summary>
    public Dictionary<string, string> ModeMaps { get; set; } = new();
    public bool SimpleMode { get; set; } = true;
    public string UiLanguage { get; set; } = "english";
    /// <summary>Language the legal notice was last fetched/accepted in.</summary>
    public string EulaLanguage { get; set; } = "";
    /// <summary>Highest accepted notice version. 0 = never accepted, so the
    /// Servers tab stays gated until the notice is accepted (Windows parity:
    /// SettingsStore.EulaVersionAccepted).</summary>
    public int EulaVersionAccepted { get; set; }
    public bool ShowClientConsoleWindow { get; set; }
    public bool OpenConsoleOnLaunch { get; set; }
    public bool UseDx12 { get; set; }
    public int DownloadLimitMbps { get; set; }
    public int DownloadConcurrency { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public string ClientWindowMode { get; set; } = "";
    /// <summary>CafeFPS channel manifest: the tip the content installer follows.</summary>
    public const string DefaultChannelUrl = "https://cdn.r5flowstate.org/channel/CHANNEL_MANIFEST.json";

    public string ChannelUrl { get; set; } = DefaultChannelUrl;
    public bool KeepLocalFiles { get; set; }

    /// <summary>Fingerprint of the notes the player has already read, so the
    /// Notes tab can show an unread dot. Windows parity:
    /// SettingsStore.NotesSeenStamp.</summary>
    public string NotesSeenStamp { get; set; } = "";

    /// <summary>Fingerprint of the blog feed the player has already read, so
    /// the Blog tab can show an unread dot. Windows parity:
    /// SettingsStore.BlogSeenStamp.</summary>
    public string BlogSeenStamp { get; set; } = "";

    /// <summary>Starred servers, one "ip:port" key per line (Windows keeps the
    /// same keys as a REG_MULTI_SZ under HKCU; the Linux settings file is JSON,
    /// so they round-trip through one delimited string). Read and written
    /// through ParseFavorites/FormatFavorites so the format stays in one place.</summary>
    public string FavoriteServers { get; set; } = "";

    /// <summary>The master server publishes no server id, so "ip:port" is the
    /// identity — the same key the Windows launcher stars and the same one a
    /// row shows. Blank input is never a favourite.</summary>
    public static string[] ParseFavorites(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var keys = new List<string>();
        foreach (var part in raw.Split('\n'))
        {
            var k = part.Trim();
            if (k.Length > 0)
                keys.Add(k);
        }
        return keys.ToArray();
    }

    /// <summary>Sorted and de-duplicated, so two launchers starring in a
    /// different order still write the same bytes.</summary>
    public static string FormatFavorites(IEnumerable<string> keys)
    {
        var unique = new List<string>();
        foreach (var key in keys)
        {
            var k = key?.Trim() ?? "";
            if (k.Length > 0 && !unique.Exists(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase)))
                unique.Add(k);
        }
        unique.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', unique);
    }

    /// <summary>Console tab command history, oldest first, one command per line.
    /// Windows keeps the same two lists as REG_MULTI_SZ values (SettingsStore
    /// .ConsoleHistoryServer/Client); the JSON file carries them as one
    /// delimited string each, through ParseHistory/FormatHistory so the format
    /// stays in one place.</summary>
    public string ConsoleHistoryServer { get; set; } = "";

    public string ConsoleHistoryClient { get; set; } = "";

    /// <summary>Oldest first, blanks dropped. Order is the point here — up-arrow
    /// walks back through it — so nothing is sorted or de-duplicated.</summary>
    public static List<string> ParseHistory(string? raw)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return lines;

        foreach (var part in raw.Split('\n'))
        {
            var cmd = part.Trim();
            if (cmd.Length > 0)
                lines.Add(cmd);
        }
        return lines;
    }

    /// <summary>Newest last, so up-arrow reaches the most recent command first
    /// the way every shell does. A command carrying a newline can never be
    /// stored: one line is one command, the same rule the tap enforces on the
    /// way to the child.</summary>
    public static string FormatHistory(IEnumerable<string> commands)
    {
        var lines = new List<string>();
        foreach (var command in commands)
        {
            var one = (command ?? "").Split('\r', '\n')[0].Trim();
            if (one.Length > 0)
                lines.Add(one);
        }
        return string.Join('\n', lines);
    }

    [JsonIgnore]
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "r5flowstate");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    public const string InstallPathEnvVar = "R5F_INSTALL_PATH";

    public static string DefaultPrefixPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games", "r5flowstate");

    public static string GetDefaultPrefixPath() => DefaultPrefixPath;

    public static string DefaultInstallPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(InstallPathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Games", "R5Flowstate");
    }

    public static LinuxSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<LinuxSettings>(json);
                if (s is not null)
                {
                    if (string.IsNullOrWhiteSpace(s.PrefixPath))
                        s.PrefixPath = DefaultPrefixPath;
                    // The window mode and the two dimensions are read back through
                    // their own rules, so a hand-edited file — or one this launcher
                    // wrote before the mode existed, which has no key at all —
                    // produces a menu item that is ticked and a launch line that
                    // carries the flag. Clamped in memory only: nothing here writes
                    // the file, the next Save does.
                    s.ClientWindowMode = DisplayModes.ClampWindowMode(s.ClientWindowMode);
                    s.ClientWidth = DisplayModes.ClampDimension(s.ClientWidth, 0);
                    s.ClientHeight = DisplayModes.ClampDimension(s.ClientHeight, 0);
                    return s;
                }
            }
        }
        catch { }
        return new LinuxSettings
        {
            InstallPath = DefaultInstallPath(),
            PrefixPath = DefaultPrefixPath,
        };
    }

    /// <summary>The spellings a Wine-era file may carry for "the EA App has its own
    /// prefix". Deliberately tolerant, and deliberately not a shared parser: the
    /// enum that used to own this is gone, and re-introducing it to read one dead
    /// key would keep a deleted concept alive for a one-time migration.</summary>
    static bool IsSplitRuntimeSpelling(string? raw)
        => (raw ?? "").Trim().ToLowerInvariant() is "wine" or "system-wine" or "systemwine"
            or "system wine" or "split" or "shared-prefix" or "shared prefix";

    /// <summary>
    /// The JSON this object would be written as. Separate from <see cref="Save"/> so
    /// the suite can assert what a migrated file looks like without writing the
    /// player's <c>settings.json</c> — which is the one file in this launcher the
    /// tests are forbidden to touch, and the migration is exactly the thing that
    /// would be tempting to prove on it.
    ///
    /// <para>The two Wine-era keys are dropped here rather than at the property,
    /// because they still have to be <em>readable</em>: this is a migration, and a
    /// settings file that loads beats a settings file that is refused.</para>
    /// </summary>
    public string Serialize()
    {
        var node = JsonSerializer.SerializeToNode(this, new JsonSerializerOptions { WriteIndented = true })!;
        node.AsObject().Remove(nameof(EaRuntime));
        node.AsObject().Remove(nameof(EaPrefixPath));
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        // Temp then move: a reader that catches the write half-done would see a
        // truncated settings file, and the next Load would answer with defaults.
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, Serialize());
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>The map a family was last played on, or null. Windows'
    /// CaptureModeSettingsFromUi/ParseModeMaps pair, in one place.</summary>
    public string? RememberedMap(string? familyId)
    {
        if (string.IsNullOrWhiteSpace(familyId) || ModeMaps.Count == 0)
            return null;
        var id = familyId.Trim();
        if (ModeMaps.TryGetValue(id, out var stem) && !string.IsNullOrWhiteSpace(stem))
            return stem;
        foreach (var pair in ModeMaps)
        {
            if (string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
                return pair.Value;
        }
        return null;
    }

    public void RememberMap(string? familyId, string? stem)
    {
        if (string.IsNullOrWhiteSpace(familyId) || string.IsNullOrWhiteSpace(stem))
            return;
        ModeMaps[familyId.Trim()] = stem.Trim();
    }

    public static bool LooksLikeInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return File.Exists(Path.Combine(path, "r5apex.exe"))
                || File.Exists(Path.Combine(path, "r5apex_ds.exe"))
                || File.Exists(Path.Combine(path, "client.dll"));
        }
        catch { return false; }
    }
}
