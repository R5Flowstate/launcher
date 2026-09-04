using System.IO;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public static class LauncherLog
{
    static readonly object Gate = new();
    static StreamWriter? s_writer;
    static string? s_writerPath;

    public static string PathFor(string installPath)
    {
        var root = string.IsNullOrWhiteSpace(installPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : installPath;
        return Path.Combine(root, ProductConstants.ContentCacheDirName, "logs", "launcher.log");
    }

    public static void Write(string installPath, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = PathFor(installPath);
                if (s_writer is null ||
                    !string.Equals(s_writerPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    s_writer?.Dispose();
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    s_writer = new StreamWriter(path, append: true) { AutoFlush = true };
                    s_writerPath = path;
                }

                s_writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message);
            }
        }
        catch
        {
            // never break the UI for logging
        }
    }
}
