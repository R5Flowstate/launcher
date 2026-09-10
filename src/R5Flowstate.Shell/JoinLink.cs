using System.IO;
using Microsoft.Win32;

namespace R5Flowstate.Shell;

/// <summary>
/// r5flowstate://join?key=&lt;listing key&gt; -- the only accepted form. The key is resolved
/// against the live server list; address, port and password never travel in the URL.
/// </summary>
public static class JoinLink
{
    public const string Scheme = "r5flowstate";
    const int KeyMax = 128;

    static string PendingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "R5Flowstate", "pending_join.txt");

    public static bool TryParse(string[] args, out string key)
    {
        key = string.Empty;
        foreach (var a in args)
        {
            if (!Uri.TryCreate(a, UriKind.Absolute, out var uri))
                continue;
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(uri.Host, "join", StringComparison.OrdinalIgnoreCase))
                continue;
            var query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0 || !part[..eq].Equals("key", StringComparison.OrdinalIgnoreCase))
                    continue;
                var v = Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
                if (v.Length == 0 || v.Length > KeyMax)
                    return false;
                // A listing key is the base64 NetKey, so '+', '/' and up to two
                // '=' of padding are part of it. Rejecting those dropped every
                // link, since a 16-byte key always ends in '=='.
                foreach (var c in v)
                    if (!char.IsAsciiLetterOrDigit(c)
                        && c is not ('-' or '_' or '.' or ':' or '+' or '/' or '='))
                        return false;
                key = v;
                return true;
            }
        }
        return false;
    }

    /// <summary>Hand a key to the already-running instance.</summary>
    public static void StashPending(string key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.WriteAllText(PendingPath, key);
        }
        catch { }
    }

    public static string? TakePending()
    {
        try
        {
            if (!File.Exists(PendingPath))
                return null;
            var key = File.ReadAllText(PendingPath).Trim();
            File.Delete(PendingPath);
            return key.Length is > 0 and <= KeyMax ? key : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Idempotent per-user registration pointing at the current exe.</summary>
    public static void RegisterProtocol()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            var command = $"\"{exe}\" \"%1\"";
            using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            if (root is null)
                return;
            root.SetValue(string.Empty, "URL:R5Flowstate");
            root.SetValue("URL Protocol", string.Empty);
            using var icon = root.CreateSubKey("DefaultIcon");
            icon?.SetValue(string.Empty, $"\"{exe}\",0");
            using var open = root.CreateSubKey(@"shell\open\command");
            if (open is null)
                return;
            if (open.GetValue(string.Empty) as string != command)
                open.SetValue(string.Empty, command);
        }
        catch { }
    }
}
