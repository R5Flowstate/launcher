using System.IO;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public static class LauncherLog
{
    static readonly object Gate = new();
    static StreamWriter? s_writer;
    static string? s_writerPath;

    /// <summary>
    /// Per-user, never under the game folder: the shell holds this handle for its
    /// whole run, and a log inside the install makes Remove game undeletable.
    /// </summary>
    public static string PathFor(string installPath)
    {
        _ = installPath;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductConstants.ProductName,
            "logs",
            "launcher.log");
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
                    // FileShare.Delete so an external cleanup can still unlink it.
                    var stream = new FileStream(
                        path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    s_writer = new StreamWriter(stream) { AutoFlush = true };
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

    /// <summary>Drop the handle before anything tries to delete a tree we may sit in.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            try { s_writer?.Dispose(); }
            catch { }
            s_writer = null;
            s_writerPath = null;
        }
    }
}
