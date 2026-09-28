namespace R5Flowstate.Linux.Core;

/// <summary>What one entry's write did. <paramref name="Problem"/> is set when
/// something went wrong; <paramref name="Written"/> is false only for a skipped
/// entry (nothing to point at).</summary>
public sealed record ShortcutWrite(string Name, string Path, bool Written, string? Problem = null);

/// <summary>Everything the EA App entry needs to know: which prefix, which exe, and
/// which Proton build runs it.</summary>
public sealed record EaAppShortcut(
    string PrefixPath,
    string EaDesktopExe,
    string ProtonDir = "");

/// <summary>
/// The menu entries the launcher installs for itself. On Windows this is the
/// setup program's job (Velopack writes the Start-menu shortcut); on Linux there
/// is no setup program, so the launcher owns it: an entry for itself, and — once
/// EA Desktop exists in the prefix — an entry for the EA App, because the game's
/// auth needs EA running and a player should not have to reach for a terminal to
/// get it up. That second one is the "launch alongside" pair.
///
/// The EA entry is written by hand rather than via <see cref="ProtonLauncher"/>
/// because a .desktop file can only carry a command line: the environment
/// ProtonLauncher sets in-process is spelled out here with <c>env</c>, and the
/// values are the same ones, so the entry and the launcher start EA identically.
/// </summary>
public static class DesktopShortcuts
{
    public const string LauncherFileName = "r5flowstate.desktop";
    public const string EaAppFileName = "r5flowstate-ea-app.desktop";
    public const string IconName = "r5flowstate";

    /// <summary>The user's home, or a refusal. With HOME unset (or pointing at a
    /// directory that does not exist) .NET answers this with an empty string, and
    /// an empty string here would silently turn every entry path into a relative
    /// one — a launcher writing <c>local/share/applications</c> into whatever
    /// directory it happens to be in. Refusing is the only safe answer; the
    /// callers already turn a thrown exception into "skipped, and here is why".</summary>
    static string Home
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                throw new InvalidOperationException(
                    "HOME is not set, so there is no applications directory to write into.");
            return home;
        }
    }

    /// <summary>Where the menus read entries from. The parameter exists so the
    /// suite writes into a temp directory instead of the player's menu.</summary>
    public static string ApplicationsDir(string? root = null) =>
        root ?? Path.Combine(Home, ".local/share/applications");

    public static string IconDir(string? root = null) =>
        Path.Combine(IconThemeDir(root), "256x256/apps");

    /// <summary>The icon theme root. <c>gtk-update-icon-cache</c> is pointed at
    /// this rather than at the directory the PNG lands in.</summary>
    public static string IconThemeDir(string? root = null) =>
        Path.Combine(root ?? Path.Combine(Home, ".local/share/icons"), "hicolor");
    /// <summary>
    /// The launcher's own entry. <paramref name="execPath"/> is the binary the
    /// menu should run — the apphost the app is running as, so the entry always
    /// points at the copy that wrote it. <paramref name="iconSourcePng"/>, when
    /// given and readable, is copied into the icon theme under
    /// <see cref="IconName"/>; a missing icon is not an error (the entry still
    /// works, it just gets a generic picture).
    /// </summary>
    public static ShortcutWrite WriteLauncher(string execPath, string? iconSourcePng = null,
        string? applicationsRoot = null, string? iconRoot = null)
    {
        var path = Path.Combine(ApplicationsDir(applicationsRoot), LauncherFileName);
        try
        {
            var icon = InstallIcon(iconSourcePng, iconRoot);
            var body = string.Join('\n',
                "[Desktop Entry]",
                "Type=Application",
                "Version=1.0",
                $"Name={ProductName()}",
                "Comment=Flowstate launcher for Apex Legends",
                $"Exec={DesktopQuote(execPath)}",
                $"Icon={IconName}",
                "Terminal=false",
                "Categories=Game;",
                "StartupNotify=false",
                $"StartupWMClass={ProductName()}",
                string.Empty);
            Write(path, body);
            return new ShortcutWrite("launcher", path, true);
        }
        catch (Exception ex)
        {
            return new ShortcutWrite("launcher", path, false, ex.Message);
        }
    }

    /// <summary>
    /// The EA App entry, for a prefix that has EA Desktop in it. Not written when
    /// there is no EA Desktop to point at: an entry that does nothing when
    /// clicked is worse than no entry.
    ///
    /// One exec shape, because there is one runtime:
    ///
    /// <code>
    /// env STEAM_COMPAT_DATA_PATH=… STEAM_COMPAT_CLIENT_INSTALL_PATH=… \
    ///     VPROJECT=1 FROM_R5F_LAUNCHER=1 <proton> run <EADesktop.exe>
    /// </code>
    ///
    /// Written by hand rather than via <see cref="ProtonLauncher"/> because a
    /// .desktop file can only carry a command line: the environment ProtonLauncher
    /// sets in-process is spelled out here with <c>env</c>, and the values are the
    /// same ones, so the entry and the launcher start EA identically.
    /// </summary>
    public static ShortcutWrite WriteEaApp(EaAppShortcut plan,
        string? applicationsRoot = null, string? iconSourcePng = null, string? iconRoot = null)
    {
        var path = Path.Combine(ApplicationsDir(applicationsRoot), EaAppFileName);
        try
        {
            if (string.IsNullOrWhiteSpace(plan.EaDesktopExe) || !File.Exists(plan.EaDesktopExe))
                return new ShortcutWrite("EA App", path, false, "EA Desktop is not in this prefix");

            var proton = ProtonLauncher.ProtonScriptFor(plan.ProtonDir);
            if (!File.Exists(proton))
                return new ShortcutWrite("EA App", path, false, "no Proton build at " + plan.ProtonDir);

            var exec = string.Join(' ',
                "env",
                "STEAM_COMPAT_DATA_PATH=" + DesktopQuote(plan.PrefixPath),
                "STEAM_COMPAT_CLIENT_INSTALL_PATH=" + DesktopQuote(ProtonLauncher.SteamRoot()),
                "VPROJECT=1",
                "FROM_R5F_LAUNCHER=1",
                DesktopQuote(proton),
                "run",
                DesktopQuote(plan.EaDesktopExe));
            var comment = "Electronic Arts client, in the Flowstate Proton prefix (needed for sign-in)";

            var icon = InstallIcon(iconSourcePng, iconRoot);
            var workDir = Path.GetDirectoryName(plan.EaDesktopExe) ?? plan.PrefixPath;
            var body = string.Join('\n',
                "[Desktop Entry]",
                "Type=Application",
                "Version=1.0",
                "Name=EA App",
                $"Comment={comment}",
                $"Exec={exec}",
                $"Path={workDir}",
                $"Icon={(icon is null ? "applications-games" : IconName)}",
                "Terminal=false",
                "Categories=Game;",
                "StartupNotify=false",
                string.Empty);
            Write(path, body);
            return new ShortcutWrite("EA App", path, true);
        }
        catch (Exception ex)
        {
            return new ShortcutWrite("EA App", path, false, ex.Message);
        }
    }

    /// <summary>The direct form the suite asserts the shape of.</summary>
    public static ShortcutWrite WriteEaApp(string protonDir, string prefixPath, string eaDesktopExe,
        string? applicationsRoot = null, string? iconSourcePng = null, string? iconRoot = null)
        => WriteEaApp(new EaAppShortcut(prefixPath, eaDesktopExe, protonDir),
            applicationsRoot, iconSourcePng, iconRoot);

    /// <summary>Both entries, for the copy of the app that is asking: EA App only
    /// when the prefix has it. This is what the Settings action and the
    /// <c>--install-shortcuts</c> lane call.
    ///
    /// <paramref name="ea"/> is the caller's own EA plan — the prefix and the exe it
    /// has already resolved. Left null, EA is looked for in
    /// <paramref name="prefixPath"/>.</summary>
    public static IReadOnlyList<ShortcutWrite> WriteAll(string execPath, string protonDir,
        string prefixPath, string? iconSourcePng = null, string? applicationsRoot = null,
        string? iconRoot = null, EaAppShortcut? ea = null)
    {
        var proton = string.IsNullOrWhiteSpace(protonDir)
            ? ProtonDiscovery.PickLatest(ProtonDiscovery.Discover())?.Dir ?? string.Empty
            : protonDir;

        if (ea is not null)
        {
            return new[]
            {
                WriteLauncher(execPath, iconSourcePng, applicationsRoot, iconRoot),
                WriteEaApp(ea, applicationsRoot, iconSourcePng, iconRoot),
            };
        }

        var found = string.IsNullOrWhiteSpace(prefixPath)
            ? null
            : ProtonLauncher.FindEaDesktopExe(prefixPath);

        return new[]
        {
            WriteLauncher(execPath, iconSourcePng, applicationsRoot, iconRoot),
            found is null
                ? new ShortcutWrite("EA App",
                    Path.Combine(ApplicationsDir(applicationsRoot), EaAppFileName), false,
                    "EA Desktop is not installed in the prefix yet")
                : WriteEaApp(proton, prefixPath, found, applicationsRoot, iconSourcePng, iconRoot),
        };
    }

    /// <summary>Take both entries off the menu (the uninstaller's step). Idempotent.</summary>
    public static void RemoveAll(string? applicationsRoot = null)
    {
        foreach (var name in new[] { LauncherFileName, EaAppFileName })
        {
            try
            {
                var path = Path.Combine(ApplicationsDir(applicationsRoot), name);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Leaving a stale entry behind is not worth failing an uninstall.
            }
        }
    }

    /// <summary>
    /// Tell the desktop that the menus changed. Both tools are optional and both
    /// are slow enough that nobody waits on one: a session that has not re-read
    /// the applications directory shows the entries after its next login anyway.
    /// Returns the tools that actually ran, for a log line.
    /// </summary>
    public static IReadOnlyList<string> RefreshCaches(string? applicationsRoot = null,
        string? iconRoot = null)
    {
        var ran = new List<string>();
        if (TryTool("update-desktop-database", ApplicationsDir(applicationsRoot)))
            ran.Add("update-desktop-database");
        if (TryTool("gtk-update-icon-cache", "-q", "-t", "-f", IconThemeDir(iconRoot)))
            ran.Add("gtk-update-icon-cache");
        return ran;
    }

    static bool TryTool(string exe, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return false;
            // A hung helper must not hang a shortcut write; nobody depends on it.
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return true;
        }
        catch
        {
            // Not installed, or not runnable: the entry is still valid.
            return false;
        }
    }

    static string? InstallIcon(string? source, string? iconRoot)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return null;
        var dir = IconDir(iconRoot);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, IconName + Path.GetExtension(source));
        File.Copy(source, target, overwrite: true);
        return target;
    }

    static void Write(string path, string body)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, body);
    }

    /// <summary>The app's own name. Linux.Core has no reference to the contracts
    /// project (it is the layer under everything), so the name is spelled here;
    /// the menu entry is the only reader and it is also the display name the
    /// launcher ships under (Linux-2.00, product "R5Flowstate").</summary>
    static string ProductName() => "R5Flowstate";

    /// <summary>A .desktop Exec field takes a command line, so anything with a
    /// space in it has to be quoted, and a quote inside a quoted argument has to
    /// be escaped. Proton's install path is the reason this exists ("Proton -
    /// Experimental").</summary>
    public static string DesktopQuote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
