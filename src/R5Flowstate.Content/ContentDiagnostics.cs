using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace R5Flowstate.Content;

/// <summary>
/// Transfer events for the launcher log: which host, what failed, how long it took.
/// Without them a player whose network blocks or throttles the CDN produces a log
/// that only says the files are broken. The sink is called from worker threads.
/// </summary>
public static class ContentDiagnostics
{
    public static Action<string>? Sink { get; set; }

    static readonly TimeSpan RetryLogInterval = TimeSpan.FromSeconds(15);
    static readonly object s_retryGate = new();
    static DateTime s_retryLoggedUtc = DateTime.MinValue;
    static int s_retriesUnlogged;

    public static void Write(string line)
    {
        try { Sink?.Invoke(line); }
        catch { /* logging must never fail a transfer */ }
    }

    /// <summary>
    /// Parallel workers retry together on a bad link, so one line per interval
    /// carries the count of the ones it stands for.
    /// </summary>
    public static void Retry(Uri uri, int attempt, TimeSpan elapsed, Exception ex)
    {
        if (Sink is null)
            return;
        int unlogged;
        lock (s_retryGate)
        {
            var now = DateTime.UtcNow;
            if (now - s_retryLoggedUtc < RetryLogInterval)
            {
                s_retriesUnlogged++;
                return;
            }
            s_retryLoggedUtc = now;
            unlogged = s_retriesUnlogged;
            s_retriesUnlogged = 0;
        }

        var line = $"Download retry: {uri.Host} attempt {attempt} at {elapsed.TotalSeconds:0}s: {Describe(ex)}";
        if (unlogged > 0)
            line += $" (+{unlogged} more retries since the last line)";
        Write(line);
    }

    /// <summary>
    /// HttpClient's top-level message is "An error occurred while sending the
    /// request." for every network failure; the cause is in the inner chain.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = ex; e is not null && seen.Count < 4; e = e.InnerException)
        {
            var part = e switch
            {
                HttpRequestException h => DescribeHttp(h),
                SocketException s => $"socket {s.SocketErrorCode}: {s.Message}",
                AuthenticationException a => "TLS: " + a.Message,
                _ => e.Message,
            };
            if (string.IsNullOrWhiteSpace(part) || !seen.Add(part))
                continue;
            if (sb.Length > 0)
                sb.Append(" <- ");
            sb.Append(part);
        }
        return sb.Length > 0 ? sb.ToString() : ex.GetType().Name;
    }

    static string DescribeHttp(HttpRequestException h)
    {
        var tags = new List<string>();
        if (h.StatusCode is { } code)
            tags.Add("HTTP " + (int)code);
        if (h.HttpRequestError != HttpRequestError.Unknown)
            tags.Add(h.HttpRequestError.ToString());
        return tags.Count > 0 ? $"{string.Join(" ", tags)}: {h.Message}" : h.Message;
    }

    public static string Bytes(long n) =>
        n >= 1_000_000_000 ? $"{n / 1_000_000_000.0:F2} GB" : $"{n / 1_000_000.0:F1} MB";
}
