using System.Globalization;

namespace R5Flowstate.Spawn;

public enum PlaylistSettingKind
{
    Int,
    Float,
    Bool,
    Choice,
}

/// <summary>
/// One host setting a playlist opens to runtime overrides:
/// <c>vars { r5f_setting_&lt;var&gt; "&lt;type&gt; [args]|&lt;label&gt;" }</c>.
/// The SDK applies the same rules on the dedi and on every client, so a value
/// that passes here is one both engines accept.
/// </summary>
public sealed class PlaylistSetting
{
    public const string DeclPrefix = "r5f_setting_";

    public required string Var { get; init; }
    public required string Label { get; init; }
    public PlaylistSettingKind Kind { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>The playlist's own value for the var; empty when it never sets it.</summary>
    public string Default { get; init; } = string.Empty;

    public static PlaylistSetting? Parse(string var, string decl, string? defaultValue)
    {
        if (string.IsNullOrWhiteSpace(var) || string.IsNullOrWhiteSpace(decl) ||
            var.StartsWith(DeclPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var bar = decl.IndexOf('|');
        var spec = bar >= 0 ? decl[..bar] : decl;
        var label = bar >= 0 && bar + 1 < decl.Length ? decl[(bar + 1)..].Trim() : var;
        var tok = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0)
            return null;

        var def = defaultValue?.Trim() ?? string.Empty;
        switch (tok[0].ToLowerInvariant())
        {
            case "bool":
                return new PlaylistSetting { Var = var, Label = label, Kind = PlaylistSettingKind.Bool, Default = def };
            case "choice":
                if (tok.Length < 2)
                    return null;
                return new PlaylistSetting
                {
                    Var = var, Label = label, Kind = PlaylistSettingKind.Choice,
                    Choices = tok.Skip(1).ToList(), Default = def,
                };
            case "int":
            case "float":
            {
                var integer = tok[0].Equals("int", StringComparison.OrdinalIgnoreCase);
                if (tok.Length < 3 || !TryNumber(tok[1], integer, out var min) || !TryNumber(tok[2], integer, out var max))
                    return null;
                return new PlaylistSetting
                {
                    Var = var, Label = label,
                    Kind = integer ? PlaylistSettingKind.Int : PlaylistSettingKind.Float,
                    Min = min, Max = max, Default = def,
                };
            }
            default:
                return null;
        }
    }

    /// <summary>Same rules as the SDK's Playlists_ValidateSetting.</summary>
    public bool Validate(string? value, out string reason)
    {
        reason = string.Empty;
        var v = value?.Trim() ?? string.Empty;
        if (v.Length == 0)
        {
            reason = "empty value";
            return false;
        }
        if (v.Length > 63)
        {
            reason = "value is longer than 63 characters";
            return false;
        }
        foreach (var c in v)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '+'))
            {
                reason = "only letters, digits and _ . + - are allowed";
                return false;
            }
        }

        switch (Kind)
        {
            case PlaylistSettingKind.Bool:
                if (v is "0" or "1")
                    return true;
                reason = "expected 0 or 1";
                return false;
            case PlaylistSettingKind.Choice:
                if (Choices.Contains(v, StringComparer.Ordinal))
                    return true;
                reason = "not one of: " + string.Join(", ", Choices);
                return false;
            default:
            {
                var integer = Kind == PlaylistSettingKind.Int;
                if (!TryNumber(v, integer, out var n))
                {
                    reason = integer ? "expected a whole number" : "expected a number";
                    return false;
                }
                if (n < Min || n > Max)
                {
                    reason = string.Format(CultureInfo.InvariantCulture, "must be {0} to {1}", Min, Max);
                    return false;
                }
                return true;
            }
        }
    }

    public bool IsDefault(string? value) =>
        string.Equals(value?.Trim() ?? string.Empty, Default, StringComparison.Ordinal);

    public string RangeText => Kind switch
    {
        PlaylistSettingKind.Int or PlaylistSettingKind.Float =>
            string.Format(CultureInfo.InvariantCulture, "{0} - {1}", Min, Max),
        _ => string.Empty,
    };

    static bool TryNumber(string s, bool integer, out double value)
    {
        value = 0;
        if (integer)
        {
            if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                return false;
            value = l;
            return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    /// <summary>
    /// The overrides a dedi launch needs: every valid value that differs from
    /// the playlist default. Invalid values are dropped, never sent.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ToOverrides(
        IReadOnlyList<PlaylistSetting> settings,
        IReadOnlyDictionary<string, string>? values)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (values is null)
            return list;
        foreach (var s in settings)
        {
            if (!values.TryGetValue(s.Var, out var v) || s.IsDefault(v) || !s.Validate(v, out _))
                continue;
            list.Add(new KeyValuePair<string, string>(s.Var, v.Trim()));
        }
        return list;
    }
}
