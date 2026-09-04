using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace R5Flowstate.Content;

/// <summary>
/// One writer per install directory.
///
/// SingleInstance covers one product per logon session, which is not the same
/// thing: fast user switching, a second Velopack install, or a portable copy all
/// get past it, and two reconcilers writing the same part file corrupt it
/// quietly. This keys on the directory instead, so two different install paths
/// can still run side by side.
/// </summary>
public sealed class InstallDirectoryLock : IDisposable
{
    readonly Mutex? _mutex;
    readonly FileStream? _file;
    readonly bool _owned;

    InstallDirectoryLock(Mutex? mutex, FileStream? file, bool owned)
    {
        _mutex = mutex;
        _file = file;
        _owned = owned;
    }

    public static string LockFilePath(string installPath) =>
        Path.Combine(installPath, ProductConstantsBridge.CacheDir, "install.lock");

    /// <summary>Null when another process holds it; <paramref name="holder"/> says who.</summary>
    public static InstallDirectoryLock? TryAcquire(string installPath, out string holder)
    {
        holder = string.Empty;
        Mutex? mutex = null;
        var owned = false;

        try
        {
            var key = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath))
                        .ToLowerInvariant())))[..16];

            // Global so it spans logon sessions, which is the case Local misses.
            mutex = new Mutex(false, @"Global\R5F.Install." + key);
            owned = mutex.WaitOne(0);
            if (!owned)
            {
                holder = ReadHolder(installPath);
                mutex.Dispose();
                return null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A mutex we cannot open is not a reason to refuse the install; the
            // lock file below still catches the common case.
            mutex?.Dispose();
            mutex = null;
            owned = false;
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        FileStream? file = null;
        try
        {
            var path = LockFilePath(installPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var me = Process.GetCurrentProcess();
            var bytes = Encoding.UTF8.GetBytes(
                $"{me.Id}\t{me.StartTime.ToUniversalTime():O}\t{Environment.MachineName}\n");
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            holder = ReadHolder(installPath);
            file?.Dispose();
            if (owned)
                mutex?.ReleaseMutex();
            mutex?.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only install root; the caller's own write will report it
            // with a better message than a lock failure would.
            file = null;
        }

        return new InstallDirectoryLock(mutex, file, owned);
    }

    static string ReadHolder(string installPath)
    {
        try
        {
            var path = LockFilePath(installPath);
            if (!File.Exists(path))
                return "another launcher";
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var parts = (reader.ReadLine() ?? string.Empty).Split('\t');
            if (parts.Length >= 3)
                return $"pid {parts[0]} on {parts[2]}";
            return "another launcher";
        }
        catch
        {
            return "another launcher";
        }
    }

    public void Dispose()
    {
        try
        {
            _file?.Dispose();
            if (_file is not null)
            {
                try { File.Delete(LockFilePathOf(_file)); }
                catch { /* best-effort */ }
            }
        }
        catch
        {
            // Releasing a lock must never throw over the real work.
        }

        if (_owned)
        {
            try { _mutex?.ReleaseMutex(); }
            catch { /* already gone */ }
        }
        _mutex?.Dispose();
    }

    static string LockFilePathOf(FileStream fs) => fs.Name;
}

/// <summary>Kept local so Contracts does not have to be referenced for one string.</summary>
internal static class ProductConstantsBridge
{
    public const string CacheDir = ".r5f";
}
