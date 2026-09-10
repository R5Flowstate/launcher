using System.Text.Json;

namespace R5Flowstate.Contracts;

public static class ChannelManifestIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ChannelManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ChannelManifest>(json, JsonOptions)
                       ?? throw new InvalidOperationException($"Failed to parse CHANNEL_MANIFEST: {path}");
        if (string.IsNullOrWhiteSpace(manifest.BaseUrl))
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            if (dir.Length > 0 &&
                dir[^1] != Path.DirectorySeparatorChar &&
                dir[^1] != Path.AltDirectorySeparatorChar)
            {
                dir += Path.DirectorySeparatorChar;
            }

            manifest.BaseUrl = new Uri(dir).AbsoluteUri;
        }

        _ = VersionIdentity.FromManifest(manifest);
        return manifest;
    }

    public static void Save(string path, ChannelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _ = VersionIdentity.FromManifest(manifest);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }
}
