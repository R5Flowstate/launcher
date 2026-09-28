using System;
using System.IO;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Where the window icon's PNG is on disk, for the one caller that needs a path
/// rather than a resource: the .desktop entry. <c>Assets/</c> is embedded in the
/// assembly (AvaloniaResource), which the window uses and a menu entry cannot,
/// so the csproj also copies this one file next to the binary.
/// </summary>
static class AppIcon
{
    internal const string FileName = "icon-mark.png";

    /// <summary>The copied PNG beside the running binary, or null when it is not
    /// there. Null is not a failure: the entry still works without an icon.</summary>
    internal static string? Source()
    {
        try
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, FileName),
                         Path.Combine(AppContext.BaseDirectory, "Assets", FileName),
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // A base directory that cannot be probed is the same as no icon.
        }
        return null;
    }
}
