namespace R5Flowstate.Linux.Core;

/// <summary>
/// The launcher's own log file. Everything the in-window Log pane shows also
/// goes here, because the pane is not evidence: it lives in memory, and the
/// moment a player closes the window the only account of what a launch did goes
/// with it. That is exactly what happened the first time the EA installer was
/// tried through Proton — Burn started, died before its window, and the reason
/// (Proton's stderr) was in a pane nobody could read any more.
///
/// It is deliberately dull: one appended line at a time, timestamped, rotated
/// once at <see cref="MaxBytes"/> so a chatty launch cannot fill a disk, and it
/// never throws — a launcher that cannot write its log still runs.
/// </summary>
public static class LauncherLog
{
    static readonly object Gate = new();
    static string? _pathOverride;

    /// <summary>Rotate at 2 MB: one previous file is kept, and that is the whole
    /// retention policy. A dedi's own output does not come through here (the
    /// console pane has its own cap); this is the launcher's account of itself.</summary>
    public static long MaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Where the log goes. Settable so the suite can point it at a
    /// temporary file instead of the player's own log.</summary>
    public static string LogPath
    {
        get => _pathOverride ?? Path.Combine(LinuxSettings.ConfigDir, "launcher.log");
        set => _pathOverride = value;
    }

    public static string RotatedPath => LogPath + ".1";

    public static void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, RotatedPath, overwrite: true);
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // No log is not a reason to fail anything.
        }
    }

    /// <summary>The last <paramref name="lines"/> lines the log holds, for a
    /// caller that wants to show them (the suite, and anything that reports a
    /// failure by quoting the run that produced it).</summary>
    public static IReadOnlyList<string> Tail(int lines)
    {
        try
        {
            if (!File.Exists(LogPath) || lines <= 0)
                return Array.Empty<string>();
            var all = File.ReadAllLines(LogPath);
            return all.Length <= lines
                ? all
                : all[(all.Length - lines)..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
