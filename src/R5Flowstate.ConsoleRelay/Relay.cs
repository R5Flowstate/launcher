using System.Net;
using System.Net.Sockets;
using System.Text;
using R5Flowstate.Spawn;

namespace R5Flowstate.ConsoleRelay;

/// <summary>
/// The bridge itself: two named pipes on one side (the game, inside the prefix) and
/// one loopback socket on the other (the launcher, outside it).
///
/// <para>Read <see cref="Program"/> for why the two transports are different. The
/// short version: the pipes are the only thing the game's console setup understands,
/// and the socket is the only channel that survives <c>proton run</c>.</para>
///
/// <para><b>Lifetime.</b> It starts when the launcher connects and ends when the
/// launcher goes away. There is no other exit: the relay does not watch the game,
/// because the launcher already owns that process and closes this socket when it
/// decides the console is done — a kill, a detach, or the launcher exiting. A
/// launcher that never connects at all is the one case the relay has to time out on
/// its own, and it does so with <c>--wait</c>.</para>
/// </summary>
static class Relay
{
    /// <summary>The one word this channel's first line starts with. The launcher's
    /// <c>HostedConsole</c> holds the same string, and the suite pins the two against
    /// each other by reading both sources: a handshake the launcher does not recognise
    /// reads as a relay that never answered.</summary>
    public const string HelloTag = "hello";

    /// <summary>The tap's own output cap, kept here so a runaway line on the command
    /// side cannot be buffered without end. The command side is far smaller in
    /// practice (the tap refuses anything over 512 characters).</summary>
    const int MaxLine = 16 * 1024;

    /// <summary>How long the relay waits for the launcher before giving up. The
    /// launcher's own deadline is shorter than this, so in practice it is the
    /// launcher that decides — this only stops an orphan holding a port forever.</summary>
    const int DefaultWaitSeconds = 60;

    static readonly object s_writeGate = new();

    public static int Run(string[] args)
    {
        var options = ReadOptions(args, out var why);
        if (options is null)
        {
            Say("[relay] " + why);
            Usage();
            return 2;
        }

        // Bound first, so a port the launcher's picker handed us and lost to another
        // process in the meantime is reported before anything is created.
        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            Say($"[relay] loopback port {options.Port} could not be bound: {ex.Message}");
            return 4;
        }

        // The pipes exist before the launcher hears anything, because the launcher's
        // next act is to start the game with their names in its environment — a game
        // that opened them before they existed would fall back to a console of its
        // own, which is the bug this process is here to remove.
        using var tap = HostedConsoleTap.Create(options.Role);

        // For a human running this by hand under Proton in a terminal. The launcher
        // never waits on it: `proton run` discards a child's stdout, which is the
        // fact the whole socket exists for (see Program's summary).
        Say($"[relay] ready port={options.Port} out={tap.OutPipePath} in={tap.InPipePath}");

        using var client = Accept(listener, options.WaitSeconds);
        if (client is null)
        {
            // Nobody came. Worth saying out loud in the log the launcher keeps: the
            // launcher's side of this is a connect that timed out, and the two lines
            // together say which end was at fault.
            Say($"[relay] no launcher connected within {options.WaitSeconds}s; exiting.");
            return 3;
        }

        return Bridge(client, tap, options.Role);
    }

    /// <summary>
    /// One client, or null when none arrives in time. The listener is closed either
    /// way: a second connection would be a second reader of the same two pipes, and
    /// the tap is a single-consumer stream.
    /// </summary>
    static TcpClient? Accept(TcpListener listener, int waitSeconds)
    {
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            return accept.Wait(TimeSpan.FromSeconds(waitSeconds)) ? accept.Result : null;
        }
        catch (AggregateException ex)
        {
            Say($"[relay] the accept failed: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
        finally
        {
            try { listener.Stop(); } catch { /* already stopped */ }
        }
    }

    static int Bridge(TcpClient client, HostedConsoleTap tap, LaunchRole role)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();

        // No BOM: the launcher's reader would take it as part of the first tag, and
        // the first tag is the handshake — a console that never starts because of
        // three bytes nobody can see is exactly the kind of failure this channel is
        // supposed to make impossible.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var outWriter = new StreamWriter(stream, utf8, 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        var inReader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);

        // The handshake carries the pipe paths rather than letting the launcher
        // reconstruct them: what the game is told to open is then, by construction,
        // what this process created. It is the first line, and every line after it is
        // the game's console, verbatim — no tags, which is why a blank console line
        // is a blank line here and not something that has to be told apart from a
        // message the launcher would rather ignore.
        Write(outWriter, HelloTag + "\t" + Tag(role) + "\t" + tap.OutPipePath + "\t" + tap.InPipePath);

        // Subscribed before Start, as every owner of a tap does: the first lines of a
        // boot are the ones that explain a launch that went wrong, and a reader that
        // starts before anyone is listening delivers them to nobody.
        tap.LineReceived += line => Write(outWriter, line);
        tap.Start();

        var commands = 0;
        while (true)
        {
            string? line;
            try
            {
                line = inReader.ReadLine();
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            // A closed socket is the normal end: the launcher disposed the tap.
            if (line is null)
                break;

            // Everything in this direction is a command, so the only thing to check is
            // that it is a command's size. The tap owns the rule (one line in, one
            // command out, 512 characters): this hands it the text and lets it refuse
            // what upstream refuses.
            if (line.Length == 0 || line.Length > MaxLine)
                continue;

            if (tap.TryWriteCommand(line))
                commands++;
        }

        Say($"[relay] the launcher closed the socket after {commands} command(s); exiting.");
        return 0;
    }

    /// <summary>The pipe-name tag, and the value <c>R5F_CONSOLE_ROLE</c> carries. The
    /// same one-character rule <see cref="HostedConsoleTap.Create"/> uses to name the
    /// pipes — the handshake reports it rather than the launcher assuming it, so a
    /// role that disagreed with the pipe names cannot happen.</summary>
    public static string Tag(LaunchRole role) => role == LaunchRole.Dedicated ? "s" : "c";

    sealed record Options(int Port, LaunchRole Role, int WaitSeconds);

    static Options? ReadOptions(string[] args, out string why)
    {
        why = string.Empty;
        var port = 0;
        var wait = DefaultWaitSeconds;
        LaunchRole? role = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (!int.TryParse(Next(args, ref i), out port) || port is < 1 or > 65535)
                    {
                        why = "the port is not a number between 1 and 65535.";
                        return null;
                    }
                    break;
                case "--role":
                    role = Next(args, ref i) switch
                    {
                        "s" => LaunchRole.Dedicated,
                        "c" => LaunchRole.Client,
                        _ => null,
                    };
                    if (role is null)
                    {
                        why = "the role is not s or c.";
                        return null;
                    }
                    break;
                case "--wait":
                    if (!int.TryParse(Next(args, ref i), out wait) || wait < 1)
                    {
                        why = "the wait is not a positive number of seconds.";
                        return null;
                    }
                    break;
                default:
                    break;
            }
        }

        if (port == 0)
            why = "no --port was given.";
        else if (role is null)
            why = "no --role was given.";

        return port != 0 && role is not null ? new Options(port, role.Value, wait) : null;
    }

    static string Next(string[] args, ref int i)
        => i + 1 < args.Length ? args[++i] : string.Empty;

    static void Usage()
    {
        Say("r5f-relay --port <n> --role <s|c> [--wait <seconds>]");
        Say("");
        Say("  --port   the loopback port the launcher connects to (it picks a free one)");
        Say("  --role   s for the dedicated server, c for the client — the tag the pipe");
        Say("           names carry, and the value R5F_CONSOLE_ROLE gets");
        Say("  --wait   how long to wait for the launcher before exiting (default "
            + DefaultWaitSeconds + "s)");
        Say("");
        Say("Exit: 0 bridged, 2 usage, 3 no launcher connected, 4 the port was taken.");
    }

    /// <summary>A line out, from whichever thread has one. One writer at a time: the
    /// tap's reader thread and the handshake share this socket, and interleaved bytes
    /// on one line would be read as one corrupt message.</summary>
    static void Write(StreamWriter writer, string line)
    {
        try
        {
            lock (s_writeGate)
            {
                writer.Write(line);
                writer.Write('\n');
                writer.Flush();
            }
        }
        catch (IOException)
        {
            // The launcher is gone; the read loop below is what ends the relay.
        }
        catch (ObjectDisposedException)
        {
            // Torn down underneath us.
        }
    }

    /// <summary>Startup and shutdown trace on stdout, which is a terminal when a
    /// human runs this by hand and nothing at all when the launcher runs it. Never
    /// throws: a relay that cannot write its own diary still has a console to
    /// carry.</summary>
    public static void Say(string line)
    {
        try
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
        catch
        {
            // No console handle, or one that is closed.
        }
    }
}
