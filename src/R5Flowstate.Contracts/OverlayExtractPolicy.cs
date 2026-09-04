namespace R5Flowstate.Contracts;

/// <summary>What a fat/platform unpack does to overlay-owned script trees.</summary>
public enum OverlayExtractPolicy
{
    WriteOfficial = 0,
    KeepEdits = 1,
}

public sealed class OverlayEditReport
{
    public List<string> Changed { get; } = new();
    public List<string> Extra { get; } = new();
    public bool HasEdits => Changed.Count > 0 || Extra.Count > 0;
    public int Total => Changed.Count + Extra.Count;
}
