using System.Text;
using System.Text.Json;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public static class PluginEnablement
{
    public const string Relative = "platform/plugins/enabled.json";
    public const int MaxEntries = 64;
    public const int MaxFileBytes = 8192;

    public static IReadOnlyList<string> Read(string installRoot)
    {
        try
        {
            if (!SafePath.TryJoin(installRoot, Relative, out var path) || !File.Exists(path))
                return Array.Empty<string>();
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return Array.Empty<string>();
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxFileBytes)
                return Array.Empty<string>();
            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static void Write(string installRoot, IReadOnlyList<string> pins)
    {
        var kept = Normalize(pins);
        if (!SafePath.TryJoin(installRoot, Relative, out var path))
            throw new InvalidOperationException("enabled.json path is outside the install");
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("enabled.json directory is missing");
        Directory.CreateDirectory(dir);
        if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("plugins directory is a reparse point");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("enabled.json is a reparse point");
        WriteAtomic(path, Encoding.ASCII.GetBytes(Format(kept)));
    }

    /// <summary>Drops enabled pins that the accepted list no longer carries.</summary>
    public static IReadOnlyList<string> Reconcile(string installRoot, CuratedList list)
    {
        var before = Read(installRoot);
        var kept = new List<string>(before.Count);
        var removed = new List<string>();
        foreach (var pin in before)
        {
            var at = pin.IndexOf('@');
            var id = pin[..at];
            var version = pin[(at + 1)..];
            var live = false;
            foreach (var plugin in list.Plugins)
            {
                if (plugin.Id == id && plugin.Version == version && !list.IsPluginRevoked(id, version))
                {
                    live = true;
                    break;
                }
            }

            if (live)
                kept.Add(pin);
            else
                removed.Add(pin);
        }

        if (removed.Count > 0)
            Write(installRoot, kept);
        return removed;
    }

    public static string? TryEnable(string installRoot, CuratedPlugin plugin, CuratedList list)
    {
        if (list.IsPluginRevoked(plugin.Id, plugin.Version))
            return "revoked";
        var listed = false;
        foreach (var item in list.Plugins)
        {
            if (item.Id == plugin.Id && item.Version == plugin.Version)
            {
                listed = true;
                break;
            }
        }

        if (!listed)
            return "not_in_catalog";
        if (!PluginInstaller.IsInstalled(installRoot, plugin))
            return "not_installed";

        var pins = Read(installRoot).Where(p => !p.StartsWith(plugin.Id + "@", StringComparison.Ordinal)).ToList();
        pins.Add(plugin.Id + "@" + plugin.Version);
        if (pins.Count > MaxEntries)
            return "full";
        Write(installRoot, pins);
        return null;
    }

    public static void Disable(string installRoot, string id)
    {
        if (!PluginStatement.IsId(id))
            throw new InvalidOperationException("plugin id is malformed");
        var prefix = id + "@";
        var kept = Read(installRoot).Where(p => !p.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Write(installRoot, kept);
    }

    public static void RemoveId(string installRoot, string id)
    {
        if (!PluginStatement.IsId(id))
            return;
        var prefix = id + "@";
        var kept = Read(installRoot).Where(p => !p.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (kept.Count != Read(installRoot).Count)
            Write(installRoot, kept);
    }

    static List<string> Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var ver) || ver != 1 ||
            !root.TryGetProperty("enabled", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var raw = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (raw.Count > MaxEntries)
                return new List<string>();
            if (item.ValueKind != JsonValueKind.String)
                return new List<string>();
            var s = item.GetString();
            if (s is null)
                return new List<string>();
            raw.Add(s);
        }

        return Normalize(raw);
    }

    static List<string> Normalize(IReadOnlyList<string> pins)
    {
        var kept = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            if (kept.Count >= MaxEntries)
                break;
            if (!PluginStatement.IsPin(pin))
                continue;
            var id = pin[..pin.IndexOf('@')];
            if (!ids.Add(id))
                continue;
            kept.Add(pin);
        }

        return kept;
    }

    static string Format(IReadOnlyList<string> pins)
    {
        var sb = new StringBuilder();
        sb.Append("{\"v\":1,\"enabled\":[");
        for (var i = 0; i < pins.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('"').Append(pins[i]).Append('"');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
