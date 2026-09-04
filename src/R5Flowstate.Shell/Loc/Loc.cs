using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace R5Flowstate.Shell;

/// <summary>
/// Launcher UI strings. JSON tables are embedded; missing keys fall back to english.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string DefaultCode = NoticeLanguages.DefaultCode;

    public static Loc Instance { get; } = new();

    static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _english = new(StringComparer.OrdinalIgnoreCase);
    string _code = DefaultCode;

    public static string Code => Instance._code;

    public static event EventHandler? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Get(key);

    public static void Initialize(string? code)
    {
        Instance.LoadEmbedded();
        Instance.Apply(NoticeLanguages.ForUi(code), notify: false);
    }

    public static string Get(string key)
    {
        if (TryGet(key, out var s))
            return s;
        return key ?? string.Empty;
    }

    public static bool TryGet(string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(key))
            return false;
        if (Instance._current.TryGetValue(key, out var s) && s.Length > 0)
        {
            value = s;
            return true;
        }
        if (Instance._english.TryGetValue(key, out var en) && en.Length > 0)
        {
            value = en;
            return true;
        }
        return false;
    }

    public static string Format(string key, params object[] args)
    {
        var fmt = Get(key);
        if (args is null || args.Length == 0)
            return fmt;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, fmt, args);
        }
        catch (FormatException)
        {
            return fmt;
        }
    }

    public static void SetLanguage(string? code)
    {
        Instance.Apply(NoticeLanguages.ForUi(code), notify: true);
    }

    void Apply(string code, bool notify)
    {
        var canon = NoticeLanguages.ForUi(code);
        if (!_tables.TryGetValue(canon, out var table))
        {
            canon = DefaultCode;
            _tables.TryGetValue(canon, out table);
        }

        _code = canon;
        _current = table ?? _english;
        if (!notify)
            return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    void LoadEmbedded()
    {
        _tables.Clear();
        var asm = Assembly.GetExecutingAssembly();
        const string prefix = "R5Flowstate.Shell.Loc.";
        const string suffix = ".json";
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var code = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                continue;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            Dictionary<string, string>? map = null;
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, s_json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (map is null)
                continue;
            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    table[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
            _tables[code] = table;
        }

        if (_tables.TryGetValue(DefaultCode, out var en))
            _english = en;
        else
            _english = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
