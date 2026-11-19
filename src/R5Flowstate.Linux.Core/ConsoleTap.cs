using System.Diagnostics;
using System.Text;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Which console a tap belongs to. Ported member-for-member from
/// <c>R5Flowstate.Spawn</c>'s <c>LaunchRole</c>, which the console tab and the
/// spawner both key off.
/// </summary>
public enum LaunchRole
{
    Client,
    Dedicated,
}

/// <summary>
/// A local console tap: the game's console as lines, and the game's stdin as
/// commands.
///
/// <para><b>It has two transports, and one of them is a fallback.</b> The real one
/// is a loopback socket to <c>r5f-relay.exe</c>, the Windows process the launcher
/// starts inside the prefix (<see cref="HostedConsole"/>): the relay creates the
/// two named pipes the game's own console setup opens — the same names the Windows
/// launcher used, <c>R5F_CONSOLE_PIPE</c> and <c>R5F_CONSOLE_IN</c> — and bridges
/// them to this end. <see cref="OverSocket"/> builds that tap.</para>
///
/// <para>The other is <see cref="Attach"/>, over a child's redirected streams, and
/// it is what a machine without the relay installed gets. It is honest about what
/// it can see: <em>not</em> the game's console. <c>proton run</c> discards a child's
/// streams (measured three ways in stage A0 — a Proton-launched helper's stdout
/// never reaches the parent while its exit code propagates normally), so this
/// transport reads Proton's own launch chatter and nothing else. That is why a
/// launch with no relay shows a nearly empty Console pane and leaves the game to
/// open console windows of its own — the two symptoms the relay exists to remove.
/// The earlier note here claimed Wine handed the child a normal inherited stdout;
/// A0 disproved it, and the claim is gone rather than left standing.</para>
///
/// <para>Everything that decides <em>what a line is</em> is upstream's, character
/// for character, on both transports: a line ends at '\n', '\r' is dropped, a line
/// that reaches 16 KB is emitted anyway, and ANSI escapes are left in the text for
/// the console to render as colour. So is the command rule (one line in, one
/// command out, no embedded newline, 512 characters). Only the two streams differ,
/// and which ones they are is the transport's business — see
/// <see cref="OverSocket"/>, which takes the reader and the sender from whoever owns
/// the channel.</para>
/// </summary>
public sealed class ConsoleTap : IDisposable
{
    const int MaxLine = 16 * 1024;
    const int CommandMax = 512;

    readonly Process _child;
    readonly StreamReader[] _sources;

    /// <summary>Whatever owns the transport this tap reads, disposed with the tap.
    /// For the socket tap that is the <see cref="HostedConsole"/> session, which owns
    /// the socket and the relay process: detaching the console is what ends them,
    /// exactly as disposing a pipe disposed the Windows tap's two ends.</summary>
    readonly IDisposable? _owned;

    readonly CancellationTokenSource _cts = new();
    StreamWriter? _in;
    Func<string, bool>? _send;
    int _disposed;
    int _started;

    public LaunchRole Role { get; }

    /// <summary>
    /// The child this tap reads, for the one question a tap cannot answer from its
    /// streams: is the process still there? Windows asks
    /// <c>ProcessSpawner.IsRoleAlive</c> (a named event inside the prefix, which a
    /// native Linux process cannot open); here the parent owns the child, so the
    /// process handle is the more direct answer to the same question. On the socket
    /// transport it is the <em>game</em>, not the relay — the relay is plumbing, and
    /// "is the dedi alive" has to keep meaning the dedi.
    /// </summary>
    public Process Child => _child;

    /// <summary>One line, raw — escapes included.</summary>
    public event Action<string>? LineReceived;

    ConsoleTap(LaunchRole role, Process child, StreamReader[] sources, IDisposable? owned)
    {
        Role = role;
        _child = child;
        _sources = sources;
        _owned = owned;
    }

    /// <summary>Attach to a child that is already running. The child must have
    /// been started with its streams captured: a tap over an inherited stream
    /// would read nothing and look exactly like a game that prints nothing, so
    /// this refuses instead of pretending.</summary>
    public static ConsoleTap Attach(LaunchRole role, Process child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!child.StartInfo.RedirectStandardOutput || !child.StartInfo.RedirectStandardError)
        {
            throw new InvalidOperationException(
                "A console tap needs a captured child: start it with "
                + "ProtonLauncher.StartCaptured (and CaptureInput for commands).");
        }

        // stdout and stderr are two streams, so the fallback transport interleaves
        // them as the pipes deliver them. The classifiers in HostReadyGate and the
        // renderer both read line by line, so neither cares.
        return new ConsoleTap(role, child, new[] { child.StandardOutput, child.StandardError },
            owned: null);
    }

    /// <summary>
    /// A tap over a channel someone else owns — today the loopback socket to the
    /// relay, which is the transport that actually carries the game's console.
    ///
    /// <para><paramref name="lines"/> is read as the game's output and
    /// <paramref name="commands"/> takes one already-cleaned command line and answers
    /// whether it went out; the framing around both is the owner's business, because
    /// the owner is the only party that knows what its transport looks like. The
    /// rules about what a line and a command <em>are</em> stay here, so they are the
    /// same on both transports. There is nothing to unwrap on this channel: the owner
    /// hands over a reader positioned after whatever handshake its protocol has, and
    /// every line after that is a console line.</para>
    /// </summary>
    public static ConsoleTap OverSocket(LaunchRole role, Process child, StreamReader lines,
        Func<string, bool> commands, IDisposable? owned = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(commands);

        var tap = new ConsoleTap(role, child, new[] { lines }, owned)
        {
            _send = commands,
        };
        return tap;
    }

    /// <summary>
    /// The readers run independently, as the two pipe threads did: sharing
    /// one would let the stdin side gate the output side, and a child that never
    /// reads a command would then show nothing at all.
    ///
    /// Idempotent, because two owners legitimately call it — whoever wires the
    /// console, and any caller that wants to be sure — and a second pass would
    /// put a second reader on each stream, which splits lines between them rather
    /// than duplicating them.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        if (_send is null && _child.StartInfo.RedirectStandardInput)
        {
            // No BOM: `Encoding.UTF8` emits one when a StreamWriter opens a
            // stream, and the child would then read it as part of its first
            // command (a dedi answers `bridge_setmode` with a syntax error, and
            // the first thing the player types fails). The Windows tap wrote
            // through `Encoding.Default`, which is UTF-8 without a preamble on
            // .NET Core — this is the same bytes on the wire.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var writer = new StreamWriter(_child.StandardInput.BaseStream, utf8, 256, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            _in = writer;
            _send = one =>
            {
                try
                {
                    writer.Write(one);
                    writer.Write('\n');
                    writer.Flush();
                    return true;
                }
                catch
                {
                    // The child is gone, or its stdin was closed under us.
                    return false;
                }
            };
        }

        var index = 0;
        foreach (var source in _sources)
        {
            // The names are the pipe threads' own. On the socket transport there is
            // one source and the second name is never used: the relay carries the
            // game's console, which is one stream, not a stdout/stderr pair.
            var name = index++ == 0 ? "r5f-console-out" : "r5f-console-err";
            new Thread(() => Pump(source)) { IsBackground = true, Name = name }.Start();
        }
    }

    /// <summary>True once the tap has somewhere to send a command.</summary>
    public bool CanCommand => Volatile.Read(ref _disposed) == 0 && _send is not null;

    /// <summary>
    /// One line in, one command out. An embedded newline would queue a second
    /// command the caller never authorised, so it ends the line instead — the
    /// rule upstream enforces at the pipe.
    /// </summary>
    public bool TryWriteCommand(string line)
    {
        var send = _send;
        if (send is null || string.IsNullOrWhiteSpace(line) || Volatile.Read(ref _disposed) != 0)
            return false;

        var cut = line.AsSpan().IndexOfAny('\r', '\n');
        var one = (cut >= 0 ? line[..cut] : line).Trim();
        if (one.Length == 0 || one.Length > CommandMax)
            return false;

        return send(one);
    }

    void Pump(StreamReader reader)
    {
        try
        {
            var acc = new StringBuilder();
            var buf = new char[1024];
            while (Volatile.Read(ref _disposed) == 0)
            {
                var n = reader.Read(buf, 0, buf.Length);
                if (n <= 0)
                    break;
                for (var i = 0; i < n; i++)
                {
                    var c = buf[i];
                    if (c == '\n')
                    {
                        Emit(acc.ToString());
                        acc.Clear();
                    }
                    else if (c != '\r')
                    {
                        if (acc.Length >= MaxLine)
                        {
                            Emit(acc.ToString());
                            acc.Clear();
                        }
                        else
                        {
                            acc.Append(c);
                        }
                    }
                }
            }

            if (acc.Length > 0)
                Emit(acc.ToString());
        }
        catch (IOException)
        {
            // The child closed the stream, or exited mid-read.
        }
        catch (ObjectDisposedException)
        {
            // Tap torn down.
        }
        catch (InvalidOperationException)
        {
            // Stream already claimed by the exit path.
        }
    }

    /// <summary>Raw, escapes included: the console renders them as colour.</summary>
    void Emit(string raw)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            // An empty line is a line: the engine prints blank lines and the pane
            // keeps them, so nothing here filters.
            LineReceived?.Invoke(raw);
        }
        catch
        {
            // A subscriber's failure must not kill the pump and lose the rest
            // of the console.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        try { _in?.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();

        // Last, so the transports have stopped being read before whatever carries
        // them goes away — a socket closed under a blocked reader is an exception
        // the pump handles, but the other order would be a pump reading a socket
        // that no longer exists at all.
        try { _owned?.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// The text with ANSI escapes removed — upstream's scanner, which knows the
    /// two forms the SDK emits: <c>ESC [ ... letter</c> (colour, and cursor
    /// moves the renderer ignores) and <c>ESC x</c>. Used by every classifier,
    /// which must see the words and never the escapes around them.
    /// </summary>
    public static string StripAnsi(string text)
    {
        if (text.IndexOf('\u001b') < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\u001b')
            {
                sb.Append(text[i]);
                continue;
            }

            if (i + 1 >= text.Length)
                break;
            if (text[i + 1] == '[')
            {
                i += 2;
                while (i < text.Length
                    && !((text[i] >= 'A' && text[i] <= 'Z') || (text[i] >= 'a' && text[i] <= 'z')))
                    i++;
            }
            else
            {
                i++;
            }
        }

        return sb.ToString();
    }
}
