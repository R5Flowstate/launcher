using System.Globalization;

namespace R5Flowstate.Shell;

public readonly record struct NoticeLanguage(string Code, string Label);

/// <summary>
/// Same allowlist as r5fms util/language.rs. Anything else is english.
/// </summary>
public static class NoticeLanguages
{
    public const string DefaultCode = "english";

    public static readonly NoticeLanguage[] All =
    {
        new("english", "English"),
        new("french", "Français"),
        new("german", "Deutsch"),
        new("italian", "Italiano"),
        new("japanese", "日本語"),
        new("polish", "Polski"),
        new("russian", "Русский"),
        new("spanish", "Español"),
        new("mspanish", "Español"),
        new("schinese", "简体中文"),
        new("tchinese", "繁體中文"),
        new("korean", "한국어"),
        new("portuguese", "Português"),
    };

    /// <summary>Player picker: every game locale, one Spanish.</summary>
    public static readonly NoticeLanguage[] Picker =
    {
        new("english", "English"),
        new("french", "Français"),
        new("german", "Deutsch"),
        new("italian", "Italiano"),
        new("japanese", "日本語"),
        new("polish", "Polski"),
        new("russian", "Русский"),
        new("spanish", "Español"),
        new("schinese", "简体中文"),
        new("tchinese", "繁體中文"),
        new("korean", "한국어"),
        new("portuguese", "Português"),
    };

    /// <summary>UI + EULA use one Spanish. mspanish still fetches if an old row is stored.</summary>
    public static string ForUi(string? code)
    {
        var canon = Sanitize(code);
        return string.Equals(canon, "mspanish", StringComparison.OrdinalIgnoreCase)
            ? "spanish"
            : canon;
    }

    public static string ResolveUi(string? uiSaved, string? eulaSaved)
    {
        if (IsAllowed(uiSaved))
            return ForUi(uiSaved);
        if (IsAllowed(eulaSaved))
            return ForUi(eulaSaved);
        return ForUi(FromOs());
    }

    public static bool IsAllowed(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        foreach (var row in All)
        {
            if (string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string Sanitize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return DefaultCode;
        var trimmed = code.Trim();
        foreach (var row in All)
        {
            if (string.Equals(row.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                return row.Code;
        }
        return DefaultCode;
    }

    public static string ResolvePreferred(string? saved) =>
        IsAllowed(saved) ? Sanitize(saved) : FromOs();

    public static string FromOs()
    {
        var culture = CultureInfo.CurrentUICulture;
        var name = culture.Name;
        var two = culture.TwoLetterISOLanguageName;

        if (name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
            return "tchinese";
        if (two.Equals("zh", StringComparison.OrdinalIgnoreCase))
            return "schinese";

        return two.ToLowerInvariant() switch
        {
            "en" => "english",
            "fr" => "french",
            "de" => "german",
            "it" => "italian",
            "ja" => "japanese",
            "pl" => "polish",
            "ru" => "russian",
            "es" => "spanish",
            "ko" => "korean",
            "pt" => "portuguese",
            _ => DefaultCode,
        };
    }
}
