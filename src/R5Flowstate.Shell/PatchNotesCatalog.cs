namespace R5Flowstate.Shell;

public sealed class PatchNoteLine
{
    /// <summary>NOTES.json marks a category with this prefix; anything else is a bullet.</summary>
    public const string HeaderPrefix = "## ";

    public required string Text { get; init; }
    public required bool IsHeader { get; init; }

    public static PatchNoteLine Parse(string raw)
    {
        var s = raw ?? string.Empty;
        var header = s.StartsWith(HeaderPrefix, StringComparison.Ordinal);
        return new PatchNoteLine
        {
            Text = header ? s[HeaderPrefix.Length..].Trim() : s,
            IsHeader = header,
        };
    }
}

public sealed class PatchNoteEntry
{
    public required string Date { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<PatchNoteLine> Items { get; init; }
}

/// <summary>Last-resort notes when NOTES.json is missing. Prefer NOTES.json.</summary>
public static class PatchNotesCatalog
{
    public static IReadOnlyList<PatchNoteEntry> All { get; } = [];
}
