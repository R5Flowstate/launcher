using System.IO.Pipes;
using System.Text;

namespace R5Flowstate.Spawn;

/// <summary>A running client asks the launcher to install a server's mods and rejoin it.</summary>
public sealed record HandoffRequest(string Host, int Port);

/// <summary>
/// Per-launcher pipe the client writes one-line requests to. The name travels
/// to the client in its environment; the pipe accepts the current user only.
/// </summary>
public sealed class LauncherHandoffListener : IDisposable
{
    public const string PipeEnv = "R5F_HANDOFF_PIPE";
    public const int MaxLineBytes = 512;

    private readonly string _leaf;
    private readonly CancellationTokenSource _cts = new();

    public event Action<HandoffRequest>? RequestReceived;

    public string PipePath => @"\\.\pipe\" + _leaf;

    private LauncherHandoffListener()
    {
        _leaf = "r5f-handoff-" + Guid.NewGuid().ToString("N");
    }

    public static LauncherHandoffListener Start()
    {
        var listener = new LauncherHandoffListener();
        _ = Task.Run(() => listener.AcceptLoopAsync(listener._cts.Token));
        return listener;
    }

    public IReadOnlyDictionary<string, string> ToEnvironment() =>
        new Dictionary<string, string>(StringComparer.Ordinal) { [PipeEnv] = PipePath };

    private async Task AcceptLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _leaf, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancel).ConfigureAwait(false);

                var line = await ReadLineCappedAsync(server, cancel).ConfigureAwait(false);
                if (line is not null && TryParse(line, out var request))
                    RequestReceived?.Invoke(request);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // The per-read timeout: a peer that connects and stalls costs one request, not the listener.
            }
            catch (IOException)
            {
                // A client that drops mid-write costs one request, not the listener.
            }
        }
    }

    private static async Task<string?> ReadLineCappedAsync(Stream stream, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        var buffer = new byte[MaxLineBytes];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), timeout.Token).ConfigureAwait(false);
            if (read <= 0)
                break;
            used += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, used) >= 0)
                break;
        }

        var end = Array.IndexOf(buffer, (byte)'\n', 0, used);
        if (end < 0)
            return null;
        return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
    }

    /// <summary><c>join_mods host:port</c>, <c>join_mods [v6]:port</c> or a bare host.</summary>
    public static bool TryParse(string line, out HandoffRequest request)
    {
        request = new HandoffRequest(string.Empty, 0);
        const string verb = "join_mods ";
        if (!line.StartsWith(verb, StringComparison.Ordinal))
            return false;

        var target = line[verb.Length..].Trim();
        string host;
        var port = 0;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 2)
                return false;
            host = target[1..close];
            var rest = target[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], out port)))
                return false;
        }
        else
        {
            var colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon)
            {
                host = target[..colon];
                if (!int.TryParse(target[(colon + 1)..], out port))
                    return false;
            }
            else
            {
                host = target;
            }
        }

        if (port is < 0 or > 65535 || !LaunchArgs.IsSafeConnectHost(host))
            return false;

        request = new HandoffRequest(host, port);
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
