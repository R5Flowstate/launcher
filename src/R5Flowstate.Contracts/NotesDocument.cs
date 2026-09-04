using System.Text.Json;
using System.Text.Json.Serialization;

namespace R5Flowstate.Contracts;

/// <summary>
/// NOTES.json — player-facing changelog on the same GH/CDN as CHANNEL.
/// Not hosted on r5fms.
/// </summary>
public sealed class NotesDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "notes";

    [JsonPropertyName("entries")]
    public List<NotesEntry> Entries { get; set; } = new();
}

public sealed class NotesEntry
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();
}

public static class NotesDocumentIO
{
    public static NotesDocument Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<NotesDocument>(json, ChannelManifestIO.JsonOptions)
                  ?? throw new InvalidOperationException("Failed to parse NOTES: " + path);
        if (doc.Entries is null)
            doc.Entries = new List<NotesEntry>();
        return doc;
    }

    public static void Save(string path, NotesDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, ChannelManifestIO.JsonOptions));
    }
}
