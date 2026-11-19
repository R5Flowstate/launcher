using System.Net;
using System.Net.Sockets;

namespace R5Flowstate.Linux.Core;

/// <summary>What a connect attempt to one loopback port came back as.
///
/// <see cref="Open"/> is the only state that means a listener is there.
/// <see cref="Refused"/> is the ordinary "nothing is listening" and is what this
/// box answers with today; the rest say the probe could not even ask.</summary>
public enum LoopbackState
{
    /// <summary>A listener accepted the connection.</summary>
    Open,

    /// <summary>The stack answered and nothing is listening on the port.</summary>
    Refused,

    /// <summary>No answer within the budget. On loopback this usually means a
    /// socket that is bound but not accepting.</summary>
    TimedOut,

    /// <summary>The address could not be reached at all — family not supported
    /// here, or no route to loopback on it.</summary>
    Unreachable,

    /// <summary>The connect raised something that is not a socket verdict.</summary>
    Error,
}

/// <summary>One port, probed on both loopback families.
/// <see cref="Family"/> is the family that answered, which matters: a Wine-hosted
/// listener may bind v4 only, and a game dialling <c>localhost</c> may resolve to
/// either.</summary>
public sealed record PortProbe(int Port, LoopbackState State, string Family, string Detail)
{
    public bool IsOpen => State == LoopbackState.Open;

    /// <summary>e.g. <c>3216 Open (IPv4)</c> or <c>3215 Refused (IPv6)</c>.</summary>
    public string Describe()
        => Family.Length > 0 ? $"{Port} {State} ({Family})" : $"{Port} {State}";
}

/// <summary>
/// Is anything listening on EA's LSX ports?
///
/// The channel between the game and the EA App is TCP on the host's loopback —
/// <c>EADesktop.exe</c>'s LSX server on <b>3216</b>, <c>EALocalHostSvc.exe</c> on
/// <b>3215</b> — not a named pipe, and Wine does not virtualize the network stack.
/// That is the whole reason the split runtime (EA under the system Wine in its own
/// prefix, the game under Proton) can work at all: two prefixes can reach each
/// other over 127.0.0.1. This class measures exactly that, and nothing else.
///
/// <para><b>Connect-level proof is necessary, not sufficient.</b> The real
/// handshake above this socket is an AES-128-ECB challenge-response against
/// <c>recipient: EbisuSDK</c>/<c>EALS</c> and the launcher holds no key material,
/// so the most an outside probe can honestly say is "something is listening and
/// accepted a connection". Whether that something is an EA LSX server willing to
/// vouch for this client is answered by the game's own log lines (see
/// <see cref="PlatformIdentityLines"/>) — never by this.</para>
///
/// The connect itself is deliberately shaped like <c>LocalRcon.ConnectLoopback</c>
/// (v6 first with <c>DualMode = false</c>, then v4), but it keeps its own copy
/// because it returns a <em>classification</em> rather than a live session, and
/// the rcon helper's job is to hand back a <c>TcpClient</c> or nothing.
/// </summary>
public static class EaChannelProbe
{
    /// <summary>The LSX server inside <c>EADesktop.exe</c> — the port a game dials
    /// into to prove its account.</summary>
    public const int LsxPort = 3216;

    /// <summary><c>EALocalHostSvc.exe</c>, EA's local host service.</summary>
    public const int LocalHostPort = 3215;

    /// <summary>Both, LSX first: 3216 is the one that decides identity.</summary>
    public static readonly IReadOnlyList<int> DefaultPorts = new[] { LsxPort, LocalHostPort };

    /// <summary>A loopback refusal is instant, so this is generous. It exists to
    /// bound a bound-but-not-accepting socket, not to wait for a slow server.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(400);

    /// <summary>Probe each port on both families. Never throws: every failure is
    /// a state.</summary>
    public static IReadOnlyList<PortProbe> Probe(
        TimeSpan? budget = null,
        IReadOnlyList<int>? ports = null)
    {
        var limit = budget ?? DefaultBudget;
        var list = ports ?? DefaultPorts;
        var results = new List<PortProbe>(list.Count);
        foreach (var port in list)
            results.Add(ProbePort(port, limit));
        return results;
    }

    public static PortProbe ProbePort(int port, TimeSpan? budget = null)
    {
        var limit = budget ?? DefaultBudget;
        var detail = new List<string>();
        var answers = new List<TryResult>(2);

        foreach (var (family, name, address) in Families)
        {
            var result = TryConnect(family, address, port, limit);
            answers.Add(result);
            detail.Add($"{name}: {result.State}{(result.Detail.Length > 0 ? " (" + result.Detail + ")" : "")}");
            if (result.State == LoopbackState.Open)
                break;
        }

        var winner = Pick(answers);
        return new PortProbe(port, winner.State, winner.Name, string.Join(", ", detail));
    }

    /// <summary>True when the port the game actually dials is answering.</summary>
    public static bool IsListening(IReadOnlyList<PortProbe> probes)
        => probes.Any(p => p.Port == LsxPort) ? probes.First(p => p.Port == LsxPort).IsOpen
                                              : probes.Any(p => p.IsOpen);

    /// <summary>
    /// The sentence the launcher shows, and whether that counts as "the channel is
    /// up". Deliberately says <c>listening</c> rather than <c>connected</c>: the
    /// probe cannot tell an EA App from anything else holding the port, and cannot
    /// see past the socket.
    ///
    /// The verdict is always about <see cref="LsxPort"/>, the port the game dials,
    /// and it agrees with <see cref="IsListening"/> by construction — a probe list
    /// that does not contain 3216 can report what it heard but cannot judge the
    /// channel, and says so rather than implying the port is closed.
    /// </summary>
    public static (string Line, bool Listening) Verdict(IReadOnlyList<PortProbe> probes)
    {
        if (probes.Count == 0)
            return ("EA channel: not probed", false);

        var lsx = probes.FirstOrDefault(p => p.Port == LsxPort);
        var listening = IsListening(probes);

        if (lsx is null)
        {
            var opener = probes.FirstOrDefault(p => p.IsOpen);
            return opener is null
                ? ($"EA channel: no listener on the ports probed, and {LsxPort} was not among them", false)
                : ($"EA channel: {opener.Port} is listening ({opener.Family}), but {LsxPort} — the port " +
                   "the game dials — was not probed", true);
        }

        if (lsx.IsOpen)
            return ($"EA channel: listening on {lsx.Port} ({lsx.Family}) — a listener accepted the connection", true);

        // Something answered, but not the port the game dials: EA's local host
        // service is up while the LSX server is not, which is a state a player can
        // reach and would otherwise read as "EA is running, so why is identity
        // missing". `listening` above is the judgement; this is the observation.
        if (probes.Any(p => p.IsOpen))
        {
            var other = probes.First(p => p.IsOpen);
            return ($"EA channel: nothing on {LsxPort}, but {other.Port} is listening ({other.Family}) " +
                    "— the LSX port the game dials is closed, so identity will not arrive", false);
        }

        var state = lsx.State;
        var why = state switch
        {
            LoopbackState.Refused => "nothing is listening",
            LoopbackState.TimedOut => "the port is bound but nothing accepted within " +
                                      $"{(int)DefaultBudget.TotalMilliseconds} ms",
            LoopbackState.Unreachable => "the loopback address was not reachable",
            LoopbackState.Error => "the connect raised an unexpected error",
            _ => "no verdict",
        };
        return ($"EA channel: {LsxPort} {state} — {why}", false);
    }

    public static string Describe(LoopbackState state) => state switch
    {
        LoopbackState.Open => "open",
        LoopbackState.Refused => "refused",
        LoopbackState.TimedOut => "timed out",
        LoopbackState.Unreachable => "unreachable",
        _ => "error",
    };

    // ------------------------------------------------------------------ plumbing

    static readonly (AddressFamily Family, string Name, IPAddress Address)[] Families =
    {
        (AddressFamily.InterNetworkV6, "IPv6", IPAddress.IPv6Loopback),
        (AddressFamily.InterNetwork, "IPv4", IPAddress.Loopback),
    };

    readonly record struct TryResult(string Name, LoopbackState State, string Detail);

    static TryResult TryConnect(AddressFamily family, IPAddress address, int port, TimeSpan budget)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient(family);
            if (family == AddressFamily.InterNetworkV6)
                client.Client.DualMode = false;

            var task = client.ConnectAsync(address, port);
            if (!task.Wait(budget))
                return new TryResult(FamilyName(family), LoopbackState.TimedOut, "");

            // Wait() wraps a failure in AggregateException; this unwraps it.
            task.GetAwaiter().GetResult();
            return new TryResult(FamilyName(family), LoopbackState.Open, "");
        }
        catch (AggregateException ae) when (ae.InnerException is SocketException inner)
        {
            return new TryResult(FamilyName(family), Classify(inner), inner.SocketErrorCode.ToString());
        }
        catch (SocketException ex)
        {
            return new TryResult(FamilyName(family), Classify(ex), ex.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return new TryResult(FamilyName(family), LoopbackState.Error, ex.GetType().Name);
        }
        finally
        {
            try { client?.Dispose(); } catch { }
        }
    }

    static LoopbackState Classify(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => LoopbackState.Refused,
        SocketError.TimedOut => LoopbackState.TimedOut,
        SocketError.NetworkUnreachable => LoopbackState.Unreachable,
        SocketError.HostUnreachable => LoopbackState.Unreachable,
        SocketError.AddressNotAvailable => LoopbackState.Unreachable,
        SocketError.AddressFamilyNotSupported => LoopbackState.Unreachable,
        SocketError.ProtocolNotSupported => LoopbackState.Unreachable,
        SocketError.OperationAborted => LoopbackState.Error,
        _ => LoopbackState.Error,
    };

    static string FamilyName(AddressFamily family)
        => family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

    /// <summary>Open beats everything; then Refused, because "the stack answered
    /// and nothing is there" is the most definite answer a refusal-less family
    /// cannot beat. An explicit loop rather than FirstOrDefault: <c>default</c> of
    /// this struct is <see cref="LoopbackState.Open"/>, so a not-found sentinel
    /// would read as a successful connect.</summary>
    static TryResult Pick(List<TryResult> answers)
    {
        foreach (var state in new[]
                 {
                     LoopbackState.Open, LoopbackState.Refused, LoopbackState.TimedOut,
                     LoopbackState.Unreachable, LoopbackState.Error,
                 })
        {
            foreach (var answer in answers)
            {
                if (answer.State == state)
                    return answer;
            }
        }
        return answers.Count > 0 ? answers[0] : new TryResult("", LoopbackState.Error, "no attempt");
    }
}
