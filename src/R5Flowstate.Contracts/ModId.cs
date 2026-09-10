using System.Text.RegularExpressions;

namespace R5Flowstate.Contracts;

/// <summary>
/// Engine mod id: letter, then 3-31 of letter/digit/dot/underscore.
/// Normalize maps '.' to '_' for script callback names, same as CModSystem.
/// </summary>
public static class ModId
{
    public const string Pattern = @"^[A-Za-z][A-Za-z0-9._]{3,31}$";

    static readonly Regex s_valid = new(
        Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && s_valid.IsMatch(id);

    public static string Normalize(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Replace('.', '_');
    }
}
