using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// <paramref name="Tree"/> hashes every file's path, size and content. <paramref name="Stamp"/> hashes
/// path, size and NTFS change time, which any write or timestamp edit resets to now; it only lets an
/// unchanged folder skip the content hash. Empty where change time is unavailable.
/// <paramref name="Modified"/> is the stamp at which the content last failed to match, so an edited
/// folder is hashed once per edit.
/// </summary>
public sealed record VerifiedRecord(string Pin, string Sha256, string Tree, string Stamp, string Modified = "");

/// <summary>
/// <c>.r5f/verified.json</c>: folders whose download matched a curated sha256. A folder counts as
/// verified only while its files are exactly the ones that download produced, and a current signed
/// list still carries that version with that sha256. Manual installs never get a record.
/// </summary>
public static class VerifiedInstalls
{
    public const string Relative = ".r5f/verified.json";
    public const int MaxEntries = 512;
    const int MaxTreeFiles = 20000;

    static readonly Regex s_folder = new(@"^[A-Za-z0-9_]{1,64}-[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);
    static readonly Regex s_hex = new(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    static readonly JsonSerializerOptions s_out = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin),
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    sealed record TreeFile(string Rel, string FullPath, long Size);

    [StructLayout(LayoutKind.Sequential)]
    struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);

    /// <summary>Every file under <paramref name="dir"/>, sorted; null on any reparse point, IO error or the file cap.</summary>
    static List<TreeFile>? Walk(string dir)
    {
        try
        {
            if (!Directory.Exists(dir) || File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                return null;
            var files = new List<TreeFile>();
            var pending = new Stack<string>();
            pending.Push(dir);
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                {
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return null;
                    if (info is DirectoryInfo sub)
                    {
                        pending.Push(sub.FullName);
                        continue;
                    }

                    if (info is not FileInfo f || files.Count >= MaxTreeFiles)
                        return null;
                    var rel = Path.GetRelativePath(dir, f.FullName).Replace('\\', '/');
                    files.Add(new TreeFile(rel, f.FullName, f.Length));
                }
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    static string? StampOf(List<TreeFile> files)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var sb = new StringBuilder();
        try
        {
            foreach (var f in files)
            {
                using var h = File.OpenHandle(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandleEx(h, 0, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()))
                    return null;
                sb.Append(f.Rel).Append('\0')
                    .Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append('\0')
                    .Append(info.ChangeTime.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    static string? TreeOf(List<TreeFile> files)
    {
        try
        {
            using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var f in files)
            {
                byte[] digest;
                using (var s = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    if (s.Length != f.Size)
                        return null;
                    digest = SHA256.HashData(s);
                }

                outer.AppendData(Encoding.UTF8.GetBytes(f.Rel + "\0" + f.Size.ToString(CultureInfo.InvariantCulture) + "\0"));
                outer.AppendData(digest);
                outer.AppendData("\n"u8);
            }

            return Convert.ToHexString(outer.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Dictionary<string, VerifiedRecord> Read(string root, Action<string>? log = null)
    {
        var map = new Dictionary<string, VerifiedRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!SafePath.TryJoin(root, Relative, out var path) || !File.Exists(path))
                return map;
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return map;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (map.Count >= MaxEntries || !s_folder.IsMatch(prop.Name))
                    continue;
                var v = prop.Value;
                if (!v.TryGetProperty("tree", out var tree) || !v.TryGetProperty("stamp", out var stamp))
                    continue;
                map[prop.Name] = new VerifiedRecord(
                    v.GetProperty("pin").GetString() ?? string.Empty,
                    v.GetProperty("sha256").GetString() ?? string.Empty,
                    tree.GetString() ?? string.Empty,
                    stamp.GetString() ?? string.Empty,
                    v.TryGetProperty("modified", out var modified) && modified.ValueKind == JsonValueKind.String
                        ? modified.GetString() ?? string.Empty
                        : string.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                       InvalidOperationException or KeyNotFoundException or FormatException)
        {
            log?.Invoke("verified.json unreadable: " + ex.Message);
        }

        return map;
    }

    /// <summary>Records <paramref name="dir"/> as the reviewed copy of <paramref name="pin"/> under <paramref name="folder"/>.</summary>
    public static void Record(string root, string folder, string pin, string sha256, string dir)
    {
        if (!s_folder.IsMatch(folder))
            throw new InvalidOperationException("Refusing verified record for folder " + folder);
        var files = Walk(dir);
        var tree = files is null ? null : TreeOf(files);
        var map = Read(root);
        if (files is null || tree is null)
            map.Remove(folder);
        else
            map[folder] = new VerifiedRecord(pin, sha256, tree, StampOf(files) ?? string.Empty);
        Write(root, map);
    }

    static void Write(string root, Dictionary<string, VerifiedRecord> map)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(MaxEntries))
        {
            var row = new JsonObject { ["pin"] = v.Pin, ["sha256"] = v.Sha256, ["tree"] = v.Tree, ["stamp"] = v.Stamp };
            if (v.Modified.Length > 0)
                row["modified"] = v.Modified;
            obj[k] = row;
        }
        if (!SafePath.TryJoin(root, Relative, out var path))
            throw new InvalidOperationException("Refusing verified.json path.");
        var dirName = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dirName))
            Directory.CreateDirectory(dirName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(s_out));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Needs a current signed list: without one nothing is reported verified. Hashes the folder
    /// only when its stamp moved.
    /// </summary>
    public static bool IsVerified(string root, InstalledMod mod, Dictionary<string, VerifiedRecord> records, CuratedList? curated)
    {
        if (curated is null)
            return false;
        var full = string.IsNullOrEmpty(mod.ThunderstoreFullName) ? mod.FolderName : mod.ThunderstoreFullName;
        var have = mod.ThunderstoreVersion;
        if (string.IsNullOrEmpty(have) ||
            !records.TryGetValue(mod.FolderName, out var rec) ||
            !string.Equals(rec.Pin, full + "-" + have, StringComparison.Ordinal) ||
            !s_hex.IsMatch(rec.Tree))
            return false;
        if (curated.IsRevoked(full, have))
            return false;
        var v = curated.FindVersion(full, have);
        if (v is null || !string.Equals(v.Sha256, rec.Sha256, StringComparison.Ordinal))
            return false;

        if (!SafePath.TryJoin(ModsStore.ModsDirectory(root), mod.FolderName, out var dir))
            return false;
        var files = Walk(dir);
        if (files is null)
            return false;
        var stamp = StampOf(files);
        if (stamp is null)
            return string.Equals(TreeOf(files), rec.Tree, StringComparison.Ordinal);
        if (string.Equals(stamp, rec.Stamp, StringComparison.Ordinal))
            return true;
        if (string.Equals(stamp, rec.Modified, StringComparison.Ordinal))
            return false;

        var same = string.Equals(TreeOf(files), rec.Tree, StringComparison.Ordinal);
        // Only a folder nothing touched while it was hashed may keep its stamp.
        if (!string.Equals(StampOf(files), stamp, StringComparison.Ordinal))
            return false;
        records[mod.FolderName] = same ? rec with { Stamp = stamp, Modified = string.Empty } : rec with { Modified = stamp };
        try
        {
            Write(root, records);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return same;
    }

    /// <summary>Pass the current list, or <see cref="CuratedCatalog.LoadLastKnown"/> when none can be checked.</summary>
    public static bool IsRevoked(InstalledMod mod, CuratedList? curated)
    {
        if (curated is null || string.IsNullOrEmpty(mod.ThunderstoreVersion))
            return false;
        var full = string.IsNullOrEmpty(mod.ThunderstoreFullName) ? mod.FolderName : mod.ThunderstoreFullName;
        return curated.IsRevoked(full, mod.ThunderstoreVersion);
    }
}
