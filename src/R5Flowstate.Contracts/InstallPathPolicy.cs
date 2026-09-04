namespace R5Flowstate.Contracts;

/// <summary>Where the game may live. Never AppData or the Velopack tree.</summary>
public static class InstallPathPolicy
{
    public const string DefaultRootName = "R5Flowstate";

    /// <summary>Setup grants Users modify on the product root, so this is
    /// writable without elevation on a wizard install.</summary>
    public static string PreferredDefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DefaultRootName,
            "Game");

    public static string UserGamesFallback =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games",
            DefaultRootName);

    public static bool IsForbidden(string? path, string? appBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return true;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
        {
            var productApp = Path.Combine(localApp, ProductConstants.ProductName);
            if (IsUnder(full, productApp) || IsUnder(full, Path.Combine(localApp, "R5Flowstate")))
                return true;
        }

        var appBase = appBaseDirectory ?? AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(appBase))
        {
            try
            {
                var dir = new DirectoryInfo(appBase);
                for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Update.exe")) ||
                        Directory.Exists(Path.Combine(dir.FullName, "packages")))
                    {
                        if (IsUnder(full, dir.FullName))
                            return true;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    public static bool TryCreateWritable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".r5f-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveFreshDefault(string? appBaseDirectory = null)
    {
        var preferred = PreferredDefaultPath;
        if (!IsForbidden(preferred, appBaseDirectory) && TryCreateWritable(preferred))
            return preferred;
        return UserGamesFallback;
    }

    public static long FreeBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return -1;
            var di = new DriveInfo(root);
            return di.IsReady ? di.AvailableFreeSpace : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Peak occupancy: planned download + largest step cache + margin.</summary>
    public static long RequiredFreeBytes(long plannedBytes, long largestStepBytes)
    {
        var margin = Math.Max(2L * 1024 * 1024 * 1024, plannedBytes / 10);
        return plannedBytes + Math.Max(0, largestStepBytes) + margin;
    }

    /// <summary>
    /// Content-addressed tracks land each object beside its destination and
    /// commit with a rename, so peak occupancy is the payload plus a margin --
    /// there is no archive and no extract pass holding a second copy.
    /// </summary>
    public static long RequiredFreeBytesInPlace(long plannedBytes)
    {
        if (plannedBytes <= 0)
            return 0;
        return plannedBytes + Math.Max(1024L * 1024 * 1024, plannedBytes / 10);
    }

    static bool IsUnder(string path, string root)
    {
        try
        {
            var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

}
