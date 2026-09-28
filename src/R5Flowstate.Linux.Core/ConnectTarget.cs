using System.Globalization;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Port of the Windows launcher's LaunchArgs.FormatConnectTarget
/// (R5Flowstate.Spawn/LaunchArgs.cs). The Windows helper lives in a
/// net8.0-windows project, and every place that shows or launches a server
/// address goes through this one form, so the port keeps its rules exactly:
/// a blank host becomes loopback, an out-of-range port falls back to the
/// default dedi port, a bare IPv6 address gets bracketed, and an address that
/// is already bracketed is left alone.
/// </summary>
public static class ConnectTarget
{
    /// <summary>The port a dedicated server uses when none is given.</summary>
    public const int DefaultDediPort = 37015;

    public static string Format(string? host, int port)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        var p = port is <= 0 or > 65535 ? DefaultDediPort : port;

        if (h.StartsWith('[') && h.Contains(']'))
            return string.Create(CultureInfo.InvariantCulture, $"{h}:{p}");

        if (h.Contains(':'))
            return string.Create(CultureInfo.InvariantCulture, $"[{h}]:{p}");

        return string.Create(CultureInfo.InvariantCulture, $"{h}:{p}");
    }

    /// <summary>
    /// A host that is a literal host and nothing else. An address on a server row
    /// comes off the master server, so it is refused unless it is alphanumerics
    /// plus <c>. : - _</c> — no spaces, no quotes, no separators a command line
    /// could split on. Rules are upstream's LaunchArgs.IsSafeConnectHost verbatim.
    /// </summary>
    public static bool IsSafeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var h = host.Trim();
        if (h.Length is 0 or > 64)
            return false;

        if (h.StartsWith('[') && h.EndsWith(']'))
            h = h[1..^1];

        var sawAlnum = false;
        foreach (var c in h)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '_';
            if (!ok)
                return false;
            if (char.IsAsciiLetterOrDigit(c))
                sawAlnum = true;
        }

        return sawAlnum;
    }

    /// <summary>
    /// A listing's address as something the game can be pointed at, or false.
    /// This is upstream's TryFormatConsoleTarget minus the console-lane comment:
    /// the same refusal, because the same untrusted pair is being written into a
    /// command the game will run.
    /// </summary>
    public static bool TryFormat(string? ip, int port, out string target)
    {
        target = string.Empty;
        if (!IsSafeHost(ip) || port is <= 0 or > 65535)
            return false;

        var host = ip!.Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        target = host.Contains(':')
            ? string.Create(CultureInfo.InvariantCulture, $"[{host}]:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
        return true;
    }
}
