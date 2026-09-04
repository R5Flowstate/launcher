using System.Diagnostics;

namespace R5Flowstate.Content;

public static class SevenZipLocator
{
    public static string Find7z()
    {
        foreach (var dir in AppDirCandidates())
        {
            foreach (var name in new[] { "7za.exe", "7z.exe" })
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full))
                    return full;
            }
        }

        foreach (var name in new[] { "7za.exe", "7za", "7z.exe", "7z" })
        {
            var onPath = FindOnPath(name);
            if (onPath is not null)
                return onPath;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
                     @"C:\Program Files\7-Zip\7z.exe",
                     @"C:\Program Files (x86)\7-Zip\7z.exe",
                     @"C:\Program Files\7-Zip\7za.exe",
                 })
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "7za.exe not found next to the launcher. Drop 7-Zip Extra 7za.exe " +
            "(and COPYING + 7za-SOURCE.txt) into the app folder. See 7za-SOURCE.txt.");
    }

    /// <summary>
    /// Kill leftover 7za (and an app-local 7z.exe) after the shell was closed.
    /// Leaves Program Files 7-Zip File Manager alone.
    /// </summary>
    public static int KillOurUnpackers()
    {
        var n = 0;
        n += KillByName("7za");

        string? ours = null;
        try { ours = Path.GetFullPath(Find7z()); }
        catch { /* no 7z next to us */ }

        if (!string.IsNullOrEmpty(ours) &&
            ours.EndsWith("7z.exe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var proc in Process.GetProcessesByName("7z"))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path is not null &&
                        path.Equals(ours, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        n++;
                    }
                }
                catch
                {
                    // access denied / exited
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        return n;
    }

    static int KillByName(string name)
    {
        var n = 0;
        foreach (var proc in Process.GetProcessesByName(name))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                n++;
            }
            catch
            {
                // gone
            }
            finally
            {
                try { proc.Dispose(); } catch { /* ignore */ }
            }
        }

        return n;
    }

    static IEnumerable<string> AppDirCandidates()
    {
        yield return AppContext.BaseDirectory;
        var dir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(dir))
            yield return Path.Combine(dir, "7za");
    }

    static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }
}
