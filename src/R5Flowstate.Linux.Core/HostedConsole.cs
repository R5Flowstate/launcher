using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// The hosted console: the launcher's end of the channel that carries the game's
/// own console into the Console tab, and the player's commands back into the game.
///
/// <para><b>The problem it solves.</b> The game's console is a Windows console
/// inside the prefix. The Windows launcher read it through two named pipes it
/// created in its own process; a native Linux launcher cannot create a Wine kernel
/// object, and it cannot read the game's streams either, because <c>proton run</c>
/// discards a child's stdout and stderr. Both of the player's symptoms come from
/// that one fact: an empty Console pane, and the game opening a console window of
/// its own because nothing hosted it.</para>
///
/// <para><b>How it is solved.</b> <c>r5f-relay.exe</c> (see
/// <c>src/R5Flowstate.ConsoleRelay</c>) is started through Proton in the
/// <em>game's own prefix</em>. It creates the two pipe ends — through
/// <c>R5Flowstate.Spawn</c>'s <c>HostedConsoleTap</c>, the very class the Windows
/// launcher uses, so the game-facing half is not a re-implementation — and bridges
/// them to a loopback TCP listener. This class connects to that listener and hands
/// the game an environment block naming those pipes.</para>
///
/// <para><b>Why loopback works across the prefix.</b> Proton's runtime makes no
/// network namespace, so <c>127.0.0.1</c> inside the prefix <em>is</em> the host's
/// loopback. That is already load-bearing here: <see cref="EaChannelProbe"/> and
/// <see cref="EaBridge"/> reach the EA App on 3216/3215 across exactly this
/// boundary.</para>
///
/// <para><b>Readiness is the accept, not a line of text.</b> The relay prints a
/// <c>[relay] ready</c> line, and it means nothing to us: a stream Proton discards
/// cannot be a signal. The green light is a successful TCP connect, and after it the
/// relay's first line is the handshake that names the two pipes it created — so the
/// game is told to open, by construction, the pipes that exist.</para>
///
/// <para><b>A relay that is not installed, or cannot start, costs a pane, never a
/// launch.</b> Every failure here returns null after one log line, and the caller
/// falls back to the transport it had before this existed.</para>
/// </summary>
public sealed class HostedConsole : IDisposable
{
    /// <summary>The contract with the game, four names — upstream's
    /// (<c>R5Flowstate.Spawn.HostedConsoleTap</c>), which the game's own console
    /// setup reads. The suite pins them against that file by reading both sources:
    /// a name that drifted would look exactly like a game that prints nothing.</summary>
    public const string HostedEnv = "R5F_HOSTED_CONSOLE";
    public const string PipeEnv = "R5F_CONSOLE_PIPE";
    public const string InEnv = "R5F_CONSOLE_IN";
    public const string RoleEnv = "R5F_CONSOLE_ROLE";

    public const string RelayExeName = "r5f-relay.exe";

    /// <summary>The word the relay's handshake starts with, mirrored in
    /// <c>src/R5Flowstate.ConsoleRelay/Relay.cs</c> — the suite reads both sources and
    /// fails if they disagree, because a handshake this end does not recognise reads
    /// as a relay that never answered.</summary>
    public const string HelloTag = "hello";

    // The channel's shape, in one place because both ends have to agree on it: the
    // relay's first line is the handshake, every line after it is the game's console
    // verbatim, and every line the launcher sends is a command. Deliberately
    // untagged: a tag would make the channel self-describing, and it would also mean
    // a console line that happened to begin with that tag had to be dropped — and
    // blank lines, which the engine prints and the pane keeps, are the case that
    // makes that unacceptable. The direction carries the meaning instead, and the
    // handshake's position is guaranteed by the relay writing it before it reads the
    // first byte of the game's output.

    /// <summary>
    /// How long the launcher waits for the relay to answer on its port. It covers
    /// the relay's own Proton start-up in a prefix that already exists — seconds,
    /// not minutes, because the relay joins the prefix the game is about to use.
    /// Deliberately short of "the player notices": a relay that cannot start must
    /// cost a couple of seconds before the launch proceeds without it.
    /// </summary>
    public static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(20);

    /// <summary>How long the handshake may take once connected. The relay writes it
    /// immediately after the accept, so this is a formality — but a blocking read
    /// still needs a deadline, or a relay that accepted and then died would hold the
    /// launch until the game's own start-up timed out.</summary>
    public static readonly TimeSpan HelloDeadline = TimeSpan.FromSeconds(5);

    readonly Process _relay;
    readonly TcpClient _client;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly string _protonDir;
    readonly string _prefixPath;
    readonly string _workingDirectory;
    readonly string _tag;
    readonly string _outPipe;
    readonly string _inPipe;

    int _disposed;
    ConsoleTap? _tap;

    HostedConsole(LaunchRole role, int port, Process relay, TcpClient client,
        StreamReader reader, StreamWriter writer, string protonDir, string prefixPath,
        string workingDirectory, string tag, string outPipe, string inPipe)
    {
        Role = role;
        Port = port;
        _relay = relay;
        _client = client;
        _reader = reader;
        _writer = writer;
        _protonDir = protonDir;
        _prefixPath = prefixPath;
        _workingDirectory = workingDirectory;
        _tag = tag;
        _outPipe = outPipe;
        _inPipe = inPipe;
    }

    public LaunchRole Role { get; }

    /// <summary>The loopback port the relay is listening on, for the log line and
    /// for a human with a packet tool.</summary>
    public int Port { get; }

    /// <summary>The game's console tap, once <see cref="StartGame"/> has started the
    /// game it reads. The caller attaches it to the Console tab.</summary>
    public ConsoleTap? Tap => _tap;

    /// <summary>The environment the <em>game</em> gets: this is what tells its console
    /// setup to use the hosted pipes instead of opening a console window of its
    /// own.</summary>
    public IReadOnlyDictionary<string, string> Environment
        => EnvironmentFor(_tag, _outPipe, _inPipe);

    /// <summary>The two pipe names, for the log.</summary>
    public string OutPipePath => _outPipe;
    public string InPipePath => _inPipe;

    /// <summary>
    /// Test seam, and it follows the same idiom as <see cref="DisplayModes"/>'s two:
    /// when nothing is set, <see cref="WinHost"/> answers. A null <em>answer</em> from
    /// a set seam therefore means "ask WinHost" rather than "there is none", so the
    /// suite stands in for a machine without the relay by pointing this at a path
    /// that is not there — which is also exactly what a half-copied install looks
    /// like.</summary>
    internal static Func<string?>? RelayPathOverride { get; set; }

    /// <summary>The relay exe, or null when it is not installed.</summary>
    internal static string? RelayPath
        => RelayPathOverride?.Invoke() ?? WinHost.Find(RelayExeName);

    /// <summary>The one-character role tag, which is also what the pipe names carry
    /// (<c>r5f-con-s-…</c>): upstream's rule, in <c>HostedConsoleTap.Create</c>.</summary>
    internal static string Tag(LaunchRole role) => role == LaunchRole.Dedicated ? "s" : "c";

    /// <summary>The relay's command line.</summary>
    internal static IReadOnlyList<string> RelayArgs(LaunchRole role, int port) => new[]
    {
        "--port", port.ToString(CultureInfo.InvariantCulture),
        "--role", Tag(role),
    };

    /// <summary>
    /// The four variables the game reads. Built from the handshake's own pipe paths
    /// rather than from a prefix this side would have to know: the Linux launcher
    /// never spells <c>\\.\pipe\</c> itself, so it cannot spell it wrongly.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> EnvironmentFor(
        string tag, string outPipe, string inPipe)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostedEnv] = "1",
            [PipeEnv] = outPipe,
            [InEnv] = inPipe,
            [RoleEnv] = tag,
        };

    /// <summary>
    /// Upstream's wipe, for a launch that has <em>no</em> relay: a child that is not
    /// tapped must not be able to open a leftover name. On Windows this overwrote
    /// values inherited from the launcher's own environment; the same reasoning
    /// holds here, where the launcher's environment is what the game inherits.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ClearedEnvironment()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostedEnv] = "0",
            [PipeEnv] = "",
            [InEnv] = "",
            [RoleEnv] = "",
        };

    /// <summary>
    /// One free loopback port, by the standard bind-and-let-go. It is a race in
    /// principle — the kernel can hand the same port to somebody else between the
    /// close and the relay's own bind — and the relay reports that case by exiting
    /// without answering, which reads here as "the relay did not answer" and costs a
    /// console pane rather than a launch. Zero on failure, which the relay refuses.
    /// </summary>
    internal static int FreePort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Connect, retrying until the deadline. The successful connect <em>is</em> the
    /// readiness signal: there is nothing else to wait for, since the relay prints
    /// its ready line to a stream Proton throws away.
    ///
    /// <para>Internal, and with the deadline passed in rather than read, because it is
    /// the one piece of this class the suite can exercise against a real listener —
    /// immediately to prove the accept, with a short deadline to prove the giving
    /// up.</para>
    /// </summary>
    internal static async Task<TcpClient?> ConnectAsync(int port, TimeSpan deadline,
        CancellationToken ct = default)
    {
        if (port <= 0)
            return null;

        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < deadline)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return client;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                return null;
            }
            catch
            {
                // Refused because the relay is not listening yet — the normal case
                // for the first few tries, and the only one worth retrying.
                client.Dispose();
            }

            try
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// The relay's first line: <c>hello&lt;TAB&gt;&lt;tag&gt;&lt;TAB&gt;&lt;out
    /// pipe&gt;&lt;TAB&gt;&lt;in pipe&gt;</c>. Everything about it is checked,
    /// because a handshake that half-parsed would put a broken path into the game's
    /// environment and look like a game with no console.
    /// </summary>
    internal static bool TryParseHello(string? line, out string tag, out string outPipe,
        out string inPipe)
    {
        tag = string.Empty;
        outPipe = string.Empty;
        inPipe = string.Empty;

        if (string.IsNullOrEmpty(line))
            return false;

        var fields = line.Split('\t');
        if (fields.Length != 4 || !string.Equals(fields[0], HelloTag, StringComparison.Ordinal))
            return false;
        if (fields[1] is not ("s" or "c"))
            return false;
        if (fields[2].Length == 0 || fields[3].Length == 0)
            return false;
        if (string.Equals(fields[2], fields[3], StringComparison.Ordinal))
            return false;

        tag = fields[1];
        outPipe = fields[2];
        inPipe = fields[3];
        return true;
    }

    /// <summary>
    /// Start the relay in the game's prefix and wait for it to answer.
    ///
    /// <para>Returns null — with one line in <paramref name="log"/> — when the relay
    /// is not installed, when there is no Proton build to run it with, when it cannot
    /// be started, when it does not answer in time, or when its handshake is not one.
    /// Every one of those is the same answer to the caller: no hosted console, and the
    /// launch proceeds the way it did before this existed.</para>
    /// </summary>
    public static async Task<HostedConsole?> StartAsync(string protonDir, string prefixPath,
        string workingDirectory, LaunchRole role, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var relay = RelayPath;
        if (relay is null || !File.Exists(relay))
        {
            log?.Invoke("The console relay is not installed (expected "
                + Path.Combine(WinHost.Dir(), RelayExeName)
                + "), so the game's own console cannot reach the Console tab. "
                + "install.sh publishes it.");
            return null;
        }

        var proton = (protonDir ?? string.Empty).Trim();
        if (proton.Length == 0 || !File.Exists(ProtonLauncher.ProtonScriptFor(proton)))
        {
            log?.Invoke("No Proton build to run the console relay through, so the game's "
                + "own console cannot reach the Console tab.");
            return null;
        }

        var prefix = (prefixPath ?? string.Empty).Trim();
        if (prefix.Length == 0)
        {
            log?.Invoke("No prefix to run the console relay in, so the game's own console "
                + "cannot reach the Console tab.");
            return null;
        }

        var port = FreePort();
        if (port == 0)
        {
            log?.Invoke("No free loopback port for the console relay, so the game's own "
                + "console cannot reach the Console tab.");
            return null;
        }

        // Deliberately `Start`, not `StartTapped`: this child's streams are going
        // nowhere (Proton discards them), so there is nothing to tap, and the pid is
        // what teardown needs.
        var options = new ProtonLauncher.ProtonRunOptions(
            ProtonDir: proton,
            PrefixPath: prefix,
            ExePath: relay,
            Args: string.Join(' ', RelayArgs(role, port)),
            WorkingDirectory: string.IsNullOrWhiteSpace(workingDirectory) ? prefix : workingDirectory);

        Process relayProcess;
        try
        {
            relayProcess = ProtonLauncher.Start(options);
        }
        catch (Exception ex)
        {
            log?.Invoke("The console relay could not be started: " + ex.Message);
            return null;
        }

        var client = await ConnectAsync(port, ConnectDeadline, ct).ConfigureAwait(false);
        if (client is null)
        {
            Kill(relayProcess);
            log?.Invoke($"The console relay did not answer on 127.0.0.1:{port} within "
                + $"{(int)ConnectDeadline.TotalSeconds}s, so the game's own console cannot "
                + "reach the Console tab.");
            return null;
        }

        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);

            // Bounded, because this is a blocking read and the relay owes us exactly
            // one line. The timeout is restored afterwards: the pump that reads the
            // console must be able to block for as long as the game is quiet.
            var originalTimeout = stream.ReadTimeout;
            stream.ReadTimeout = (int)HelloDeadline.TotalMilliseconds;
            string? hello;
            try
            {
                hello = reader.ReadLine();
            }
            finally
            {
                stream.ReadTimeout = originalTimeout;
            }

            if (!TryParseHello(hello, out var tag, out var outPipe, out var inPipe))
            {
                reader.Dispose();
                client.Dispose();
                Kill(relayProcess);
                log?.Invoke("The console relay answered with something that is not its "
                    + "handshake, so the game's own console cannot reach the Console tab: "
                    + (hello ?? "(nothing at all)"));
                return null;
            }

            var writer = new StreamWriter(stream, utf8, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            return new HostedConsole(role, port, relayProcess, client, reader, writer,
                proton, prefix, workingDirectory, tag, outPipe, inPipe);
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { /* ignore */ }
            Kill(relayProcess);
            log?.Invoke("The console relay's handshake could not be read: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Start the game itself, with the hosted pipes in its environment and a tap on
    /// this channel.
    ///
    /// <para>Untapped and uncaptured on purpose: the game's streams go to Proton,
    /// which discards them, and the console now arrives over the socket instead. A
    /// captured stream nobody reads is a child that can block on a full pipe — the
    /// exact hazard <c>ProtonRunOptions.CaptureInput</c> exists to avoid.</para>
    /// </summary>
    public Process StartGame(string exePath, string args,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (_tap is not null)
            throw new InvalidOperationException("This hosted console already started a game.");
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(HostedConsole));

        var child = ProtonLauncher.Start(new ProtonLauncher.ProtonRunOptions(
            ProtonDir: _protonDir,
            PrefixPath: _prefixPath,
            ExePath: exePath,
            Args: args,
            WorkingDirectory: _workingDirectory,
            ExtraEnv: MergeEnvironment(extraEnv, Environment)));

        // The child is the game, not the relay: "is the dedi alive" has to keep
        // meaning the dedi. The session is owned by the tap, so detaching the
        // console kills the relay and closes the socket with it.
        _tap = ConsoleTap.OverSocket(Role, child, _reader, SendCommand, owned: this);
        return child;
    }

    /// <summary>The launcher's half of the command direction: one already-cleaned line
    /// out. Returns false when it could not go out, which the console tab reports to
    /// the player rather than pretending it was delivered.
    ///
    /// <para>Locked, because the handshake and the tap's own reader thread are not the
    /// only writers in principle — and interleaved bytes on one line would reach the
    /// game as one corrupt command.</para>
    /// </summary>
    bool SendCommand(string one)
    {
        try
        {
            lock (_writer)
            {
                _writer.Write(one);
                _writer.Write('\n');
                _writer.Flush();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Extra environment with the hosted block on top: these names are this
    /// channel's own, so nothing a caller passes may shadow them. Public because the
    /// caller's other blocks (the local RCON session's, for instance) go through the
    /// same merge.</summary>
    public static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string>? extra, IReadOnlyDictionary<string, string> hosted)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach (var kv in extra)
                merged[kv.Key] = kv.Value;
        }
        foreach (var kv in hosted)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The reader first: it releases the pump's blocked read, and the pump is the
        // only other party here. Then the socket, then the relay. Disposing the tap
        // arrives here through `owned`, so this path runs exactly once per launch.
        try { _reader.Dispose(); } catch { /* ignore */ }
        try { _writer.Dispose(); } catch { /* ignore */ }
        try { _client.Dispose(); } catch { /* ignore */ }
        Kill(_relay);
    }

    static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or never ours to kill.
        }
    }
}
