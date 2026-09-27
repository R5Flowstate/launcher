namespace R5Flowstate.Contracts;

public sealed class InstalledMod
{
    public string FolderName { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int Order { get; set; }
    public string Realm { get; init; } = string.Empty;
    public bool HasScripts { get; init; }
    public string? IconPath { get; init; }
    public string ThunderstoreVersion { get; init; } = string.Empty;
    public string ThunderstoreFullName { get; init; } = string.Empty;

    /// <summary>Stock content the mod declares it replaces, e.g. <c>datatable survival_loot</c>.</summary>
    public IReadOnlyList<string> Replaces { get; init; } = Array.Empty<string>();

    /// <summary>Maps the mod declares in <c>"Maps"</c>, e.g. <c>mp_team_mod__arena</c>.</summary>
    public IReadOnlyList<string> Maps { get; init; } = Array.Empty<string>();
}
