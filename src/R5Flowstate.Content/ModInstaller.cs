using System.IO.Compression;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Installs a Thunderstore (or local) zip into mods/. Extraction is the hostile path:
/// every entry goes through SafePath.TryJoin; the whole archive is refused on zip-slip,
/// forbidden extension, entry cap, size cap, or compression-ratio ceiling.
/// </summary>
public sealed class ModInstaller
{
    public const int MaxZipEntries = 5000;
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxCompressionRatio = 100;

    static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".asi", ".sys", ".bat", ".cmd", ".ps1", ".scr", ".msi", ".com",
    };

    readonly ThunderstoreClient _thunderstore;

    public ModInstaller(ThunderstoreClient thunderstore)
    {
        _thunderstore = thunderstore ?? throw new ArgumentNullException(nameof(thunderstore));
    }

    public Task<InstalledMod> InstallAsync(
        string installPath,
        ModPackage package,
        string? versionNumber = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var version = PickVersion(package, versionNumber);
        if (string.IsNullOrWhiteSpace(version.DownloadUrl))
            throw new InvalidOperationException("Package version has no download URL.");
        var folder = string.IsNullOrWhiteSpace(package.FullName)
            ? package.Owner + "-" + package.Name
            : package.FullName;
        return InstallFromUrlAsync(installPath, version.DownloadUrl, folder, progress, cancel);
    }

    public async Task<InstalledMod> InstallFromUrlAsync(
        string installPath,
        string url,
        string? folderName = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        var modsDir = ModsStore.ModsDirectory(installPath);
        Directory.CreateDirectory(modsDir);
        var stamp = NewStamp();
        if (!SafePath.TryJoin(modsDir, ".__mod_" + stamp + ".zip", out var zipPath))
            throw new InvalidOperationException("Refusing staging zip path.");

        try
        {
            progress?.Report(new ContentInstallProgress
            {
                Phase = "download",
                Message = "Downloading mod package",
            });
            await _thunderstore.DownloadAsync(url, zipPath, progress, cancel).ConfigureAwait(false);
            return await InstallFromZipAsync(installPath, zipPath, folderName, progress, cancel)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    public async Task<InstalledMod> InstallFromZipAsync(
        string installPath,
        string zipPath,
        string? folderName = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
            throw new ArgumentException("Zip path is required.", nameof(zipPath));
        if (zipPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            zipPath = ChannelSource.LocalPath(zipPath);
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Mod zip not found.", zipPath);

        var modsDir = ModsStore.ModsDirectory(installPath);
        Directory.CreateDirectory(modsDir);
        var stamp = NewStamp();
        if (!SafePath.TryJoin(modsDir, ".__mod_" + stamp, out var staging))
            throw new InvalidOperationException("Refusing staging directory path.");

        try
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(new ContentInstallProgress
            {
                Phase = "extract",
                Message = "Extracting mod package",
            });

            var nestedName = ExtractValidated(zipPath, staging, progress, cancel);
            PromoteIfNested(staging);
            if (!SafePath.TryJoin(staging, ModsStore.ModSettingsFileName, out var vdfPath) ||
                !File.Exists(vdfPath))
            {
                throw new InvalidOperationException(
                    "Archive has no " + ModsStore.ModSettingsFileName + " after extract.");
            }

            var doc = ModVdf.Parse(File.ReadAllText(vdfPath));
            var id = doc.Get("id");
            if (string.IsNullOrEmpty(id) || !ModId.IsValid(id))
            {
                throw new InvalidOperationException(
                    "Archive " + ModsStore.ModSettingsFileName +
                    " is missing a valid id (got '" + (id ?? "") + "').");
            }

            var destName = ChooseFolderName(folderName, nestedName, Path.GetFileName(zipPath));
            if (!SafePath.IsSafeRelative(destName, out var reason))
                throw new InvalidOperationException("Refusing dest folder name (" + reason + "): " + destName);
            if (!SafePath.TryJoin(modsDir, destName, out var dest))
                throw new InvalidOperationException("Refusing dest path outside mods/: " + destName);

            var destParent = Path.GetDirectoryName(dest);
            if (!string.Equals(destParent, Path.GetFullPath(modsDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Mod folder must be a direct child of mods/.");

            progress?.Report(new ContentInstallProgress
            {
                Phase = "commit",
                Message = "Installing " + id,
            });

            foreach (var existing in ModsStore.Discover(installPath))
            {
                if (!string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(existing.FolderName, destName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!SafePath.TryJoin(modsDir, existing.FolderName, out var oldDir))
                    continue;
                ModsStore.DeleteDirectoryNoReparse(oldDir);
            }

            if (Directory.Exists(dest))
                ModsStore.DeleteDirectoryNoReparse(dest);

            Directory.Move(staging, dest);
            ModsStore.AddOrEnable(installPath, id);

            var installed = ModsStore.Discover(installPath)
                .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            return installed ?? throw new InvalidOperationException(
                "Mod installed but was not discovered: " + id);
        }
        finally
        {
            TryDeleteTree(staging);
        }
    }

    static ModPackageVersion PickVersion(ModPackage package, string? versionNumber)
    {
        var versions = package.Versions ?? Array.Empty<ModPackageVersion>();
        if (versions.Count == 0)
            throw new InvalidOperationException("Package has no versions.");
        if (string.IsNullOrWhiteSpace(versionNumber))
            return versions[0];
        foreach (var v in versions)
        {
            if (string.Equals(v.VersionNumber, versionNumber, StringComparison.OrdinalIgnoreCase))
                return v;
        }

        throw new InvalidOperationException("Package has no version " + versionNumber + ".");
    }

    static string ExtractValidated(
        string zipPath,
        string staging,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        Directory.CreateDirectory(staging);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxZipEntries)
            throw new InvalidOperationException(
                "Archive has " + zip.Entries.Count + " entries; cap is " + MaxZipEntries + ".");

        long totalUncompressed = 0;
        string? nested = null;
        var nestedAmbiguous = false;

        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (IsDirectory(entry))
                continue;
            if (IsSymlink(entry))
                throw new InvalidOperationException("Archive contains a symlink entry; refusing.");

            var relative = NormalizeEntryName(entry.FullName);
            if (!SafePath.TryJoin(staging, relative, out _))
            {
                throw new InvalidOperationException(
                    "Archive entry escapes staging (zip-slip): " + entry.FullName);
            }

            var ext = Path.GetExtension(relative);
            if (ForbiddenExtensions.Contains(ext))
            {
                throw new InvalidOperationException(
                    "Archive contains a forbidden file type (" + ext + "): " + relative);
            }

            var uncompressed = entry.Length;
            if (uncompressed < 0)
                throw new InvalidOperationException("Archive entry has a negative size: " + relative);
            var compressed = entry.CompressedLength;
            if (uncompressed > MaxUncompressedBytes)
                throw new InvalidOperationException("Archive entry exceeds size cap: " + relative);
            if (compressed > 0 && uncompressed > compressed * (long)MaxCompressionRatio)
            {
                throw new InvalidOperationException(
                    "Archive entry compression ratio exceeds cap: " + relative);
            }

            if (compressed <= 0 && uncompressed > 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "Archive entry has no compressed size but a large payload: " + relative);
            }

            totalUncompressed += uncompressed;
            if (totalUncompressed > MaxUncompressedBytes)
                throw new InvalidOperationException("Archive uncompressed size exceeds 2 GiB cap.");

            var top = TopSegment(relative);
            if (top.Length > 0)
            {
                if (nested is null)
                    nested = top;
                else if (!string.Equals(nested, top, StringComparison.OrdinalIgnoreCase))
                    nestedAmbiguous = true;
            }
        }

        long written = 0;
        var fileIndex = 0;
        var fileCount = zip.Entries.Count(e => !IsDirectory(e));
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            if (IsDirectory(entry))
                continue;

            var relative = NormalizeEntryName(entry.FullName);
            if (!SafePath.TryJoin(staging, relative, out var dest))
                throw new InvalidOperationException("Archive entry escapes staging: " + entry.FullName);

            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using var input = entry.Open();
            using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;
                written += read;
                if (copied > entry.Length)
                    throw new InvalidOperationException("Archive entry exceeded declared size: " + relative);
                if (written > MaxUncompressedBytes)
                    throw new InvalidOperationException("Archive uncompressed size exceeds 2 GiB cap.");
                output.Write(buffer, 0, read);
            }

            if (ModsStore.IsReparsePoint(dest))
            {
                TryDeleteFile(dest);
                throw new InvalidOperationException("Extracted a reparse point; refusing: " + relative);
            }

            fileIndex++;
            progress?.Report(new ContentInstallProgress
            {
                Phase = "extract",
                Unit = ProgressUnit.Items,
                Current = fileIndex,
                Total = fileCount,
                Message = relative,
            });
        }

        return nestedAmbiguous ? string.Empty : nested ?? string.Empty;
    }

    static void PromoteIfNested(string staging)
    {
        string[] dirs;
        string[] files;
        try
        {
            dirs = Directory.GetDirectories(staging);
            files = Directory.GetFiles(staging);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not inspect staging directory.", e);
        }

        if (files.Length != 0 || dirs.Length != 1)
            return;

        var inner = dirs[0];
        if (ModsStore.IsReparsePoint(inner))
            throw new InvalidOperationException("Refusing to promote a reparse-point folder.");

        string[] innerDirs;
        string[] innerFiles;
        try
        {
            innerDirs = Directory.GetDirectories(inner);
            innerFiles = Directory.GetFiles(inner);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not inspect nested archive folder.", e);
        }

        foreach (var child in innerFiles.Concat(innerDirs))
        {
            var name = Path.GetFileName(child);
            if (!SafePath.TryJoin(staging, name, out var dest))
                throw new InvalidOperationException("Refusing nested path outside staging: " + name);
            Directory.Move(child, dest);
        }

        Directory.Delete(inner, recursive: false);
    }

    static string ChooseFolderName(string? requested, string nested, string zipFileName)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();

        var fromNested = StripVersionSuffix(nested);
        if (!string.IsNullOrWhiteSpace(fromNested) && SafePath.IsSafeRelative(fromNested, out _))
            return fromNested;

        var fromZip = zipFileName;
        if (fromZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fromZip = fromZip[..^4];
        fromZip = StripVersionSuffix(fromZip);
        if (!string.IsNullOrWhiteSpace(fromZip) &&
            !fromZip.StartsWith(".__mod_", StringComparison.OrdinalIgnoreCase) &&
            SafePath.IsSafeRelative(fromZip, out _))
        {
            return fromZip;
        }

        throw new InvalidOperationException(
            "Could not derive a mods/ folder name; pass Owner-Name explicitly.");
    }

    static string StripVersionSuffix(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var last = name.LastIndexOf('-');
        if (last <= 0 || last >= name.Length - 1)
            return name;
        var suffix = name[(last + 1)..];
        if (suffix.Length > 0 && char.IsDigit(suffix[0]))
            return name[..last];
        return name;
    }

    static string NormalizeEntryName(string fullName)
    {
        var rel = fullName.Replace('\\', '/').Trim();
        while (rel.StartsWith("./", StringComparison.Ordinal))
            rel = rel[2..];
        return rel;
    }

    static string TopSegment(string relative)
    {
        var slash = relative.IndexOf('/');
        return slash < 0 ? string.Empty : relative[..slash];
    }

    static bool IsDirectory(ZipArchiveEntry entry) =>
        string.IsNullOrEmpty(entry.Name) ||
        entry.FullName.EndsWith('/') ||
        entry.FullName.EndsWith('\\');

    static bool IsSymlink(ZipArchiveEntry entry)
    {
        const int unixSIfLnk = 0xA000;
        var mode = (int)((entry.ExternalAttributes >> 16) & 0xFFFF);
        if ((mode & 0xF000) == unixSIfLnk)
            return true;
        return false;
    }

    static string NewStamp() =>
        DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N")[..8];

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                ModsStore.DeleteDirectoryNoReparse(dir);
        }
        catch
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
