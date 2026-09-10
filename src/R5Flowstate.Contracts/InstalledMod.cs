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
    public bool ClientSafe { get; init; }
    public bool HasScripts { get; init; }
    public string? IconPath { get; init; }
    public string ThunderstoreVersion { get; init; } = string.Empty;
    public string ThunderstoreFullName { get; init; } = string.Empty;
}
