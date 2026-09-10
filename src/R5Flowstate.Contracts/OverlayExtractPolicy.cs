using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// Identity of the edited set, by path only. Content is deliberately not in
    /// it: re-saving a file the player already chose to keep is not a new
    /// decision, but editing a file they have never been asked about is.
    /// </summary>
    public string Fingerprint()
    {
        var paths = Changed
            .Concat(Extra)
            .Select(p => OverlayPaths.Norm(p).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", paths))));
    }
}
