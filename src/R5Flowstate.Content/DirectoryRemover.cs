using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public sealed record RemoveFailure(string Path, string Reason);

public sealed record RemoveReport(
    int FilesDeleted,
    long BytesDeleted,
    bool RootRemoved,
    IReadOnlyList<RemoveFailure> Failures);

/// <summary>
/// Deletes a game install without the all-or-nothing behaviour of
/// Directory.Delete: one locked file must not strand the other 40 GB. Every
/// failure is collected and reported so the caller can name what is left.
/// </summary>
public static class DirectoryRemover
{
    const int LockedRetries = 3;
    const int RetryDelayMs = 250;

    /// <summary>
    /// True only for a tree the launcher itself downloaded. The uninstall hook
    /// runs unattended, so an adopted or hand-built install is left alone: a kept
    /// folder is recoverable, a wrongly erased one is not.
    /// </summary>
    public static bool LooksLikeOurInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(full))
            return false;
        if (IsProtectedLocation(full))
            return false;

        return File.Exists(Path.Combine(full, ProductConstants.InstallStateFileName));
    }

    /// <summary>
    /// Refuses drive roots, the profile root, and the well-known shell folders.
    /// A bad install path must not turn a removal into a disaster.
    /// </summary>
    public static bool IsProtectedLocation(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return true;
        }

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        if (string.IsNullOrEmpty(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                 })
        {
            var special = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(special))
                continue;
            if (string.Equals(
                    Path.GetFullPath(special).TrimEnd(Path.DirectorySeparatorChar),
                    full,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static RemoveReport Remove(
        string root,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var failures = new List<RemoveFailure>();
        var files = 0;
        var bytes = 0L;

        string full;
        try
        {
            full = Path.GetFullPath(root);
        }
        catch (Exception ex)
        {
            return new RemoveReport(0, 0, false, new[] { new RemoveFailure(root, ex.Message) });
        }

        if (!Directory.Exists(full))
            return new RemoveReport(0, 0, true, Array.Empty<RemoveFailure>());

        if (IsProtectedLocation(full))
        {
            return new RemoveReport(0, 0, false,
                new[] { new RemoveFailure(full, "Refused: not a folder this launcher may delete.") });
        }

        foreach (var file in EnumerateFiles(full, failures))
        {
            ct.ThrowIfCancellationRequested();
            long size = 0;
            try { size = new FileInfo(file).Length; }
            catch { }

            if (TryDeleteFile(file, out var reason))
            {
                files++;
                bytes += size;
                if (files % 250 == 0)
                    progress?.Report(file);
            }
            else
            {
                failures.Add(new RemoveFailure(file, reason));
            }
        }

        // Deepest first: a directory only goes once everything under it has.
        foreach (var dir in EnumerateDirectories(full, failures)
                     .OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            TryDeleteDirectory(dir);
        }

        var rootRemoved = TryDeleteDirectory(full);
        if (!rootRemoved && failures.Count == 0)
            failures.Add(new RemoveFailure(full, "Folder is in use."));

        return new RemoveReport(files, bytes, rootRemoved, failures);
    }

    static IEnumerable<string> EnumerateFiles(string root, List<RemoveFailure> failures)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            failures.Add(new RemoveFailure(root, ex.Message));
            return Array.Empty<string>();
        }
    }

    static IEnumerable<string> EnumerateDirectories(string root, List<RemoveFailure> failures)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            failures.Add(new RemoveFailure(root, ex.Message));
            return Array.Empty<string>();
        }
    }

    static bool TryDeleteFile(string path, out string reason)
    {
        reason = string.Empty;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return true;
                if (info.IsReadOnly)
                    info.IsReadOnly = false;
                info.Delete();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = ex.Message;
                if (attempt >= LockedRetries)
                    return false;
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }
    }

    static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return true;
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
