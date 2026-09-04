using System.Text;

namespace R5Flowstate.Contracts;

public sealed class ModVdfBlock
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<KeyValuePair<string, string>> Entries { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
}

public sealed class ModVdfDocument
{
    public static ModVdfDocument Empty { get; } = new();

    public string RootKey { get; init; } = string.Empty;

    public IReadOnlyList<KeyValuePair<string, string>> Entries { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    public IReadOnlyList<ModVdfBlock> Blocks { get; init; } = Array.Empty<ModVdfBlock>();

    public string? Get(string key)
    {
        string? found = null;
        foreach (var kv in Entries)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                found = kv.Value;
        }
        return found;
    }
}

/// <summary>
/// Order-preserving KeyValues subset: one named root, quoted pairs, one level of sub-blocks.
/// Malformed input yields an empty document rather than an exception.
/// </summary>
public static class ModVdf
{
    public static ModVdfDocument Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ModVdfDocument.Empty;

        try
        {
            var scanner = new Scanner(text);
            if (!scanner.TryReadQuoted(out var rootKey) || rootKey.Length == 0)
                return ModVdfDocument.Empty;
            if (!scanner.TryReadChar('{'))
                return ModVdfDocument.Empty;

            var entries = new List<KeyValuePair<string, string>>();
            var blocks = new List<ModVdfBlock>();

            while (true)
            {
                scanner.SkipWsAndComments();
                if (scanner.TryReadChar('}'))
                    break;
                if (scanner.AtEnd)
                    return ModVdfDocument.Empty;

                if (!scanner.TryReadQuoted(out var key) || key.Length == 0)
                    return ModVdfDocument.Empty;

                scanner.SkipWsAndComments();
                if (scanner.TryReadChar('{'))
                {
                    if (!TryReadBlockBody(scanner, out var blockEntries))
                        return ModVdfDocument.Empty;
                    blocks.Add(new ModVdfBlock { Name = key, Entries = blockEntries });
                    continue;
                }

                if (!scanner.TryReadQuoted(out var value))
                    return ModVdfDocument.Empty;
                entries.Add(new KeyValuePair<string, string>(key, value));
            }

            return new ModVdfDocument
            {
                RootKey = rootKey,
                Entries = entries,
                Blocks = blocks,
            };
        }
        catch
        {
            return ModVdfDocument.Empty;
        }
    }

    public static string Write(string rootKey, IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(entries);

        var sb = new StringBuilder();
        sb.Append('"').Append(Escape(rootKey)).Append('"').Append('\n');
        sb.Append("{\n");
        foreach (var kv in entries)
        {
            sb.Append('\t');
            sb.Append('"').Append(Escape(kv.Key)).Append('"');
            sb.Append('\t');
            sb.Append('"').Append(Escape(kv.Value)).Append('"');
            sb.Append('\n');
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    public static List<(string Id, bool Enabled)> ReadModList(string text)
    {
        var doc = Parse(text);
        var list = new List<(string Id, bool Enabled)>(doc.Entries.Count);
        foreach (var kv in doc.Entries)
        {
            if (string.IsNullOrEmpty(kv.Key))
                continue;
            list.Add((kv.Key, IsTruthy(kv.Value)));
        }
        return list;
    }

    public static string WriteModList(IEnumerable<(string Id, bool Enabled)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Write("ModList", entries.Select(e =>
            new KeyValuePair<string, string>(e.Id, e.Enabled ? "1" : "0")));
    }

    public static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var v = value.Trim();
        return v == "1"
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryReadBlockBody(Scanner scanner, out List<KeyValuePair<string, string>> entries)
    {
        entries = new List<KeyValuePair<string, string>>();
        while (true)
        {
            scanner.SkipWsAndComments();
            if (scanner.TryReadChar('}'))
                return true;
            if (scanner.AtEnd)
                return false;

            if (!scanner.TryReadQuoted(out var key) || key.Length == 0)
                return false;

            scanner.SkipWsAndComments();
            if (scanner.TryReadChar('{'))
            {
                if (!scanner.SkipBalancedBrace())
                    return false;
                continue;
            }

            if (!scanner.TryReadQuoted(out var value))
                return false;
            entries.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { '\\', '"' }) < 0)
            return value;
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    sealed class Scanner
    {
        readonly string _text;
        int _i;

        public Scanner(string text)
        {
            _text = text;
            if (_text.Length > 0 && _text[0] == '\uFEFF')
                _i = 1;
        }

        public bool AtEnd => _i >= _text.Length;

        public void SkipWsAndComments()
        {
            while (_i < _text.Length)
            {
                var c = _text[_i];
                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    _i++;
                    continue;
                }

                if (c == '/' && _i + 1 < _text.Length && _text[_i + 1] == '/')
                {
                    _i += 2;
                    while (_i < _text.Length && _text[_i] != '\n')
                        _i++;
                    continue;
                }

                break;
            }
        }

        public bool TryReadChar(char expected)
        {
            SkipWsAndComments();
            if (_i >= _text.Length || _text[_i] != expected)
                return false;
            _i++;
            return true;
        }

        public bool TryReadQuoted(out string value)
        {
            SkipWsAndComments();
            value = string.Empty;
            if (_i >= _text.Length || _text[_i] != '"')
                return false;
            _i++;
            var sb = new StringBuilder();
            while (_i < _text.Length)
            {
                var c = _text[_i++];
                if (c == '"')
                {
                    value = sb.ToString();
                    return true;
                }

                if (c == '\\' && _i < _text.Length)
                {
                    sb.Append(_text[_i++]);
                    continue;
                }

                sb.Append(c);
            }

            return false;
        }

        /// <summary>Consume a block whose opening brace was already read, including nested braces.</summary>
        public bool SkipBalancedBrace()
        {
            var depth = 1;
            var inQuote = false;
            while (_i < _text.Length && depth > 0)
            {
                var c = _text[_i++];
                if (inQuote)
                {
                    if (c == '\\' && _i < _text.Length)
                    {
                        _i++;
                        continue;
                    }

                    if (c == '"')
                        inQuote = false;
                    continue;
                }

                if (c == '"')
                {
                    inQuote = true;
                    continue;
                }

                if (c == '{')
                    depth++;
                else if (c == '}')
                    depth--;
            }

            return depth == 0;
        }
    }
}
