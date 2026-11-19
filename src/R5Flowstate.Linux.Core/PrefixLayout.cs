namespace R5Flowstate.Linux.Core;

/// <summary>
/// Where things live inside a compat prefix. One layout now, because there is one
/// runtime: Proton.
///
/// <code>
/// compatdata root:  &lt;prefix&gt;/pfx/drive_c     (STEAM_COMPAT_DATA_PATH=&lt;prefix&gt;)
/// </code>
///
/// <para>This used to be two — a Wine prefix keeps <c>drive_c</c> directly under
/// WINEPREFIX — and every reader had to be told which runtime it was looking at.
/// With the system Wine gone there is nothing to disambiguate, so the parameter is
/// gone with it and the one remaining caller cannot get it wrong.</para>
///
/// <para>The <c>drive_c</c>-directly-under-the-root case is still honoured, because
/// it is not a Wine-ism: it is what a <em>prefix path pointed at a prefix</em>
/// looks like, which a player can still have in settings from the Wine era and
/// which the art compatdata could produce if it were ever pointed at a bare
/// prefix. The one that exists wins; that is what this helper has always done.</para>
/// </summary>
public static class PrefixLayout
{
    /// <summary>The directory Wine treats as the prefix root — <c>pfx</c> under a
    /// compatdata root, which is what every prefix this launcher creates is.</summary>
    public static string WinePrefixOf(string prefixPath) => Path.Combine(prefixPath, "pfx");

    /// <summary>Wine's C: drive inside a compatdata root.</summary>
    public static string DriveCFor(string prefixPath)
    {
        var direct = Path.Combine(prefixPath, "drive_c");
        return Directory.Exists(direct) ? direct : Path.Combine(prefixPath, "pfx", "drive_c");
    }

    public static string SystemRegPath(string winePrefix)
        => Path.Combine(winePrefix, "system.reg");

    /// <summary>True when wineboot has already built this prefix. The file's
    /// absence is what separates "nothing here yet" from "something else lives in
    /// this directory", which is a refusal rather than a bootstrap.</summary>
    public static bool IsBootstrapped(string winePrefix)
    {
        try
        {
            return File.Exists(SystemRegPath(winePrefix));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The arch wineboot recorded ("win64"/"win32"), or null when there is
    /// no prefix to read. Wine writes it as a <c>#arch=</c> line near the top of
    /// system.reg, which is a plain text file — no Wine API needed to read it.</summary>
    public static string? DeclaredArch(string winePrefix)
    {
        try
        {
            var path = SystemRegPath(winePrefix);
            if (!File.Exists(path))
                return null;

            var lines = 0;
            foreach (var raw in File.ReadLines(path))
            {
                if (++lines > 40)
                    break;
                var line = raw.Trim();
                if (line.StartsWith("#arch=", StringComparison.OrdinalIgnoreCase))
                    return line["#arch=".Length..].Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when Proton itself built this prefix, read from the marker it
    /// writes once <c>setup_prefix</c> has copied its default prefix in and stamped a
    /// machine GUID (<c>creation_sync_guard</c>).
    ///
    /// <para>This matters because a prefix that only <em>Wine</em> bootstrapped is a
    /// different thing: Proton's first run fills a bare prefix in from
    /// <c>files/share/default_pfx</c> and writes its own bookkeeping, and the launcher
    /// must not be the one to decide that a Proton prefix should start life as a bare
    /// Wine one. Anything that writes into the game's prefix asks this first.</para></summary>
    public static bool IsProtonPrefix(string winePrefix)
    {
        try
        {
            return File.Exists(Path.Combine(winePrefix, "creation_sync_guard"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="dir"/> has anything in it at all. Used to
    /// tell an empty directory (safe to bootstrap) from an occupied one (not).</summary>
    public static bool HasEntries(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A Linux path as the Windows program inside the prefix has to hear it.
    ///
    /// <para>Wine maps drive <c>Z:</c> to the host's <c>/</c>, so
    /// <c>/home/x/art</c> is <c>Z:\home\x\art</c> from inside the prefix. This is
    /// what lets the launcher hand the art host a cache directory on the real
    /// filesystem: the host writes there through the same drive the launcher reads
    /// it from, so no copy is needed to get the pixels back.</para>
    ///
    /// <para>Anything that does not look like a Linux absolute path — already
    /// Windows-shaped, or relative — is returned unchanged. Guessing at those would
    /// be inventing a mapping, and a caller that passes one has its own reason.</para>
    /// </summary>
    public static string ToZDrive(string linuxPath)
    {
        if (string.IsNullOrWhiteSpace(linuxPath))
            return linuxPath ?? string.Empty;

        var path = linuxPath.Trim();

        // Already a Windows path (drive letter or UNC): leave it alone.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return path;

        // Relative: nothing to map to.
        if (!path.StartsWith('/'))
            return path;

        return @"Z:" + path.Replace('/', '\\');
    }
}
