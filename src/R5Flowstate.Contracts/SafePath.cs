namespace R5Flowstate.Contracts;

/// <summary>
/// Joins a manifest-supplied relative path onto the install root. Every path we
/// write, delete or stat comes from a downloaded document, so containment is
/// checked here rather than trusted at each call site.
/// </summary>
public static class SafePath
{
    const char Backslash = (char)92;

    static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsSafeRelative(string? relative, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(relative))
        {
            reason = "empty path";
            return false;
        }

        var rel = relative.Replace(Backslash, '/').Trim();
        if (rel.StartsWith('/') || Path.IsPathRooted(rel))
        {
            reason = "absolute path";
            return false;
        }

        // A drive-relative form such as "C:file" is rooted on some APIs and not
        // on others; reject the colon outright, which also covers NTFS streams.
        if (rel.Contains(':', StringComparison.Ordinal))
        {
            reason = "drive or stream qualifier";
            return false;
        }

        foreach (var segment in rel.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0)
                continue;
            if (segment == "." || segment == "..")
            {
                reason = "relative segment";
                return false;
            }
            if (segment[^1] == '.' || segment[^1] == ' ')
            {
                reason = "trailing dot or space";
                return false;
            }
            if (segment.Any(c => c < 32))
            {
                reason = "control character";
                return false;
            }

            var stem = segment;
            var dot = stem.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
                stem = stem[..dot];
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                reason = "reserved device name";
                return false;
            }
        }

        return true;
    }

    public static bool TryJoin(string root, string? relative, out string full)
    {
        full = string.Empty;
        if (!IsSafeRelative(relative, out _))
            return false;

        try
        {
            var rootFull = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(
                Path.Combine(rootFull, relative!.Replace('/', Path.DirectorySeparatorChar)));

            var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            full = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Join(string root, string? relative)
    {
        if (!TryJoin(root, relative, out var full))
        {
            IsSafeRelative(relative, out var reason);
            throw new InvalidOperationException(
                $"Refusing path outside install root ({(reason.Length > 0 ? reason : "escapes root")}): {relative}");
        }
        return full;
    }

    /// <summary>
    /// Win32 long-path form. Beyond 259 characters the plain path fails on APIs
    /// that have not opted in to long paths, and an install root is chosen by
    /// the player.
    /// </summary>
    public static string Extended(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return fullPath;
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return Path.IsPathFullyQualified(fullPath) ? @"\\?\" + fullPath : fullPath;
    }
}
