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
    public const int MaxFolderDepth = 8;

    // Content types a mod can legitimately ship. Anything else refuses the whole archive:
    // a blocklist always misses the next executable format.
    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".nut", ".gnut", ".rson", ".txt", ".vdf", ".cfg", ".json", ".md", ".csv", ".res", ".menu",
        ".png", ".jpg", ".jpeg", ".webp", ".dds",
        ".rpak", ".starpak", ".mbnk", ".mstr", ".mprj", ".raw_hdr",
        ".bsp", ".bsp_lump", ".ent", ".kv", ".vpk", ".nm", ".ain",
        ".ttf", ".otf",
    };

    readonly ThunderstoreClient _thunderstore;

    public ModInstaller(ThunderstoreClient thunderstore)
    {
        _thunderstore = thunderstore ?? throw new ArgumentNullException(nameof(thunderstore));
    }

    /// <summary>Catalog packages must declare <c>mod.vdf</c> id <c>Owner.Name</c>.</summary>
    public static string ExpectedCatalogId(ModPackage package) =>
        (package.Owner ?? string.Empty) + "." + (package.Name ?? string.Empty);

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
        if (string.IsNullOrWhiteSpace(package.Owner) || string.IsNullOrWhiteSpace(package.Name))
            throw new InvalidOperationException("Package has no owner or name.");

        var expectedId = ExpectedCatalogId(package);
        if (!ModId.IsValid(expectedId))
        {
            throw new InvalidOperationException(
                "Package " + package.Owner + "-" + package.Name + " cannot be installed: its mod id '" +
                expectedId + "' must be 4-32 letters, digits, '.' or '_'.");
        }

        var folder = package.Owner + "-" + package.Name;
        var binding = new CatalogBinding(expectedId, package.Name, version.VersionNumber);
        return InstallFromUrlCoreAsync(installPath, version.DownloadUrl, folder, binding, progress, cancel);
    }

    public Task<InstalledMod> InstallFromUrlAsync(
        string installPath,
        string url,
        string? folderName = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default) =>
        InstallFromUrlCoreAsync(installPath, url, folderName, binding: null, progress, cancel);

    async Task<InstalledMod> InstallFromUrlCoreAsync(
        string installPath,
        string url,
        string? folderName,
        CatalogBinding? binding,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
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
            return await InstallFromZipCoreAsync(installPath, zipPath, folderName, binding, progress, cancel)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    public Task<InstalledMod> InstallFromZipAsync(
        string installPath,
        string zipPath,
        string? folderName = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default) =>
        InstallFromZipCoreAsync(installPath, zipPath, folderName, binding: null, progress, cancel);

    async Task<InstalledMod> InstallFromZipCoreAsync(
        string installPath,
        string zipPath,
        string? folderName,
        CatalogBinding? binding,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
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

            var vdfText = ModsStore.ReadModSettingsText(vdfPath) ?? throw new InvalidOperationException(
                "Archive " + ModsStore.ModSettingsFileName + " is larger than the game accepts.");
            var doc = ModVdf.Parse(vdfText);
            var id = doc.Get("id");
            if (string.IsNullOrEmpty(id) || !ModId.IsValid(id))
            {
                var shown = id is { Length: > 40 } ? id[..40] + "..." : id ?? "";
                throw new InvalidOperationException(
                    "Archive " + ModsStore.ModSettingsFileName +
                    " is missing a valid id (got '" + shown + "').");
            }

            if (binding is not null)
                VerifyCatalogBinding(staging, id, binding);

            VerifyOwnedFiles(installPath, staging, id, doc);

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

            // The engine keys mods by id with '.' folded to '_'; two folders sharing that key
            // disable each other, and replacing another author's folder would be a takeover.
            var normalizedId = ModId.Normalize(id);
            foreach (var existing in ModsStore.Discover(installPath))
            {
                if (!string.Equals(ModId.Normalize(existing.Id), normalizedId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(existing.FolderName, destName, StringComparison.OrdinalIgnoreCase))
                    continue;
                throw new InvalidOperationException(
                    "Mod id '" + id + "' is already used by the installed mod in '" + existing.FolderName +
                    "'. Uninstall it first.");
            }

            if (Directory.Exists(dest))
            {
                // Replacing a folder is an update of the same mod, never a swap to another id.
                if (SafePath.TryJoin(dest, ModsStore.ModSettingsFileName, out var oldVdf) && File.Exists(oldVdf))
                {
                    var oldText = ModsStore.ReadModSettingsText(oldVdf);
                    var oldId = oldText is null ? null : ModVdf.Parse(oldText).Get("id");
                    if (!string.Equals(oldId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Folder '" + destName + "' holds mod '" + oldId + "', not '" + id + "'. Uninstall it first.");
                    }
                }

                ModsStore.DeleteDirectoryNoReparse(dest);
            }

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

    // Same rule the engine applies at load; refusing here reports it at install time.
    static void VerifyOwnedFiles(string installPath, string staging, string id, ModVdfDocument doc)
    {
        var nameSpace = ModOwnership.Namespace(id);
        if (!ModOwnership.IsUsableNamespace(nameSpace))
            throw new InvalidOperationException("Mod id '" + id + "' reads as '" + ModOwnership.Separator + "' in file names; pick another id.");

        var maps = ModOwnership.ReadMaps(doc, nameSpace, out var mapError)
                   ?? throw new InvalidOperationException("Archive " + ModsStore.ModSettingsFileName + ": " + mapError + ".");
        foreach (var map in maps)
        {
            if (BaseShipsMap(installPath, map))
                throw new InvalidOperationException("Package declares map '" + map + "', which the game already ships.");
        }

        var tables = ModOwnership.ReadDatatableOverrides(doc, out var tableError)
                     ?? throw new InvalidOperationException("Archive " + ModsStore.ModSettingsFileName + ": " + tableError + ".");
        if (ModOwnership.ReadLocalizationOverrides(doc, out var locError) is null)
            throw new InvalidOperationException("Archive " + ModsStore.ModSettingsFileName + ": " + locError + ".");

        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (rel.Count(c => c == '/') > MaxFolderDepth)
                throw new InvalidOperationException("Package nests folders deeper than " + MaxFolderDepth + ": " + rel);
            if (!ModOwnership.OwnsPath(nameSpace, maps, tables, rel, installPath))
            {
                throw new InvalidOperationException(
                    "Package ships '" + rel + "', outside its namespace '" + nameSpace +
                    "' and its declared maps.");
            }
        }
    }

    static bool BaseShipsMap(string installPath, string map)
    {
        if (File.Exists(Path.Combine(installPath, "maps", map + ".bsp")) ||
            File.Exists(Path.Combine(installPath, "paks", "Win64", map + ".rpak")))
            return true;

        var vpkDir = Path.Combine(installPath, "vpk");
        return Directory.Exists(vpkDir) &&
               Directory.EnumerateFiles(vpkDir, "*" + map + ".bsp.pak000_dir.vpk").Any();
    }

    sealed record CatalogBinding(string ExpectedId, string PackageName, string VersionNumber);

    static void VerifyCatalogBinding(string staging, string id, CatalogBinding binding)
    {
        if (!string.Equals(id, binding.ExpectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Package mod.vdf id '" + id + "' does not match its Thunderstore name; expected '" +
                binding.ExpectedId + "'.");
        }

        if (!SafePath.TryJoin(staging, ModsStore.ManifestFileName, out var manPath) || !File.Exists(manPath))
            throw new InvalidOperationException("Package has no " + ModsStore.ManifestFileName + ".");

        var man = ModsStore.ReadManifest(manPath)
                  ?? throw new InvalidOperationException("Package " + ModsStore.ManifestFileName + " is malformed.");
        if (!string.Equals(man.Name, binding.PackageName, StringComparison.Ordinal) ||
            !string.Equals(man.VersionNumber, binding.VersionNumber, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Downloaded package is " + man.Name + " " + man.VersionNumber + ", expected " +
                binding.PackageName + " " + binding.VersionNumber + ".");
        }
    }

    // MZ (PE) and ELF; a renamed binary is refused whatever its extension.
    static bool LooksExecutable(byte[] head, int length) =>
        (length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z') ||
        (length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F');

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

    /// <summary>Entry count from the (Zip64) end-of-central-directory record; -1 when absent.</summary>
    static long DeclaredZipEntryCount(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        var tailLength = (int)Math.Min(fs.Length, 22 + 0xFFFF + 20);
        var tail = new byte[tailLength];
        fs.Seek(-tailLength, SeekOrigin.End);
        fs.ReadExactly(tail);

        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (tail[i] != 0x50 || tail[i + 1] != 0x4B || tail[i + 2] != 0x05 || tail[i + 3] != 0x06)
                continue;

            long total = BitConverter.ToUInt16(tail, i + 10);
            if (total != 0xFFFF || i < 20)
                return total;

            // Zip64: the locator sits right before the EOCD and points at the Zip64 record.
            var loc = i - 20;
            if (tail[loc] != 0x50 || tail[loc + 1] != 0x4B || tail[loc + 2] != 0x06 || tail[loc + 3] != 0x07)
                return total;

            var zip64Offset = BitConverter.ToInt64(tail, loc + 8);
            if (zip64Offset < 0 || zip64Offset + 56 > fs.Length)
                return long.MaxValue;

            var rec = new byte[56];
            fs.Seek(zip64Offset, SeekOrigin.Begin);
            fs.ReadExactly(rec);
            if (rec[0] != 0x50 || rec[1] != 0x4B || rec[2] != 0x06 || rec[3] != 0x06)
                return long.MaxValue;

            var total64 = BitConverter.ToUInt64(rec, 32);
            return total64 > long.MaxValue ? long.MaxValue : (long)total64;
        }

        return -1;
    }

    static string ExtractValidated(
        string zipPath,
        string staging,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        Directory.CreateDirectory(staging);

        // ZipFile.OpenRead materialises every entry the central directory declares, so the
        // count is checked from the end-of-central-directory record before opening.
        var declaredEntries = DeclaredZipEntryCount(zipPath);
        if (declaredEntries > MaxZipEntries)
            throw new InvalidOperationException(
                "Archive declares " + declaredEntries + " entries; cap is " + MaxZipEntries + ".");

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
            if (!AllowedExtensions.Contains(ext))
            {
                throw new InvalidOperationException(
                    "Archive contains a file type mods may not ship (" + ext + "): " + relative);
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
            var head = new byte[4];
            var headLen = 0;
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                // A decompressor may hand back one byte at a time; judge the full magic.
                if (headLen < head.Length)
                {
                    var take = Math.Min(head.Length - headLen, read);
                    Array.Copy(buffer, 0, head, headLen, take);
                    headLen += take;
                    if (LooksExecutable(head, headLen))
                        throw new InvalidOperationException("Archive contains an executable image: " + relative);
                }

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
            // Each report is one UI-thread post that re-lays out the status line; the entry
            // names come from the package.
            if (fileIndex == fileCount || fileIndex % 50 == 1)
            {
                progress?.Report(new ContentInstallProgress
                {
                    Phase = "extract",
                    Unit = ProgressUnit.Items,
                    Current = fileIndex,
                    Total = fileCount,
                    Message = relative.Length > 120 ? relative[..120] + "..." : relative,
                });
            }
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
