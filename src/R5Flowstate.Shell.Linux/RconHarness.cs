using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// A netcon host for the self-test: a loopback listener that answers the
/// launcher's RCON the way a dedi's own frame loop does. It exists for the two
/// things the rest of the suite cannot reach — a <c>Ping</c> that comes back
/// <em>answered</em>, and a <c>bridge_setmode</c> that actually goes out on the
/// wire — because both need something listening on the port the launcher dials,
/// and the suite must never start the game to get one.
///
/// It is deliberately independent of the port's own reader: it parses the
/// request fields upstream's builder writes (messageId, requestType, the request
/// text and its extra line) with its own varint reader, and writes its own reply
/// frames. A harness that shared the code under test would prove nothing about
/// the wire; this one is the other end of it, written from the same bytes.
///
/// The knobs are the three states a real host can be in: answering, refusing the
/// password (a dedi configured with a different secret), and accepting without
/// ever answering — which is what a hung frame loop looks like from the
/// launcher's side, and the case the 1.5 s ping timeout exists for.
/// </summary>
sealed class RconHarness : IDisposable
{
    // Host-order INetCon.h RCON_FRAME_MAGIC, written big-endian: the same bytes
    // NetconPlaintext sends and reads.
    const uint FrameMagic = 0x6E6F4352;

    readonly TcpListener _listener;
    readonly Thread _accept;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentQueue<(string Verb, string Line)> _commands = new();
    readonly object _gate = new();
    string _password = "";
    int _disposed;

    /// <summary>Every post-auth request this host was sent, in order: the verb
    /// and its line, as the dedi's own console would have received them.</summary>
    public (string Verb, string Line)[] Commands => _commands.ToArray();

    /// <summary>The password the last connection authenticated with — what the
    /// dedi would have read out of its own environment.</summary>
    public string LastPassword
    {
        get { lock (_gate) return _password; }
    }

    /// <summary>Answer "auth incorrect" instead of "auth successful".</summary>
    public bool RefuseAuth { get; set; }

    /// <summary>Accept connections and never answer: the hung frame loop.</summary>
    public bool Silent { get; set; }

    public int Port { get; }

    public RconHarness(int port)
    {
        Port = port;
        // The client dials ::1 first and only falls back to 127.0.0.1, so an
        // IPv6 loopback listener is the end it will reach.
        _listener = new TcpListener(IPAddress.IPv6Loopback, port);
        _listener.Start();
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "r5f-rcon-harness" };
        _accept.Start();
    }

    void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch
            {
                return; // the listener was closed
            }

            new Thread(() => Serve(client)) { IsBackground = true, Name = "r5f-rcon-conn" }.Start();
        }
    }

    void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 8000;
                stream.WriteTimeout = 8000;

                while (!_cts.IsCancellationRequested)
                {
                    var frame = ReadFrame(stream);
                    if (frame is null)
                        return;

                    if (!TryParseRequest(frame, out var type, out var msg, out var extra))
                        continue;

                    if (type != 0)
                    {
                        // The password frame: the one the dedi checks, and the
                        // one that is never written to argv.
                        lock (_gate) _password = msg;
                        if (Silent)
                        {
                            SleepUntilDisposed();
                            return;
                        }
                        WriteReply(stream, RefuseAuth ? "auth incorrect" : "auth successful");
                        continue;
                    }

                    _commands.Enqueue((msg, extra));
                    // No reply to a command: the launcher's Exec does not read
                    // one (the engine's own reply would land in the socket
                    // buffer and the launcher closes the connection).
                }
            }
        }
        catch
        {
            // A closed connection is how every one of these ends.
        }
    }

    void SleepUntilDisposed()
    {
        while (!_cts.IsCancellationRequested)
            Thread.Sleep(50);
    }

    static byte[]? ReadFrame(NetworkStream stream)
    {
        var head = ReadExact(stream, 8);
        if (head is null)
            return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(head) != FrameMagic)
            return null;
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (len <= 0 || len > 65536)
            return null;
        return ReadExact(stream, len);
    }

    /// <summary>
    /// The launcher's reply frame: the same magic and length, and an envelope
    /// whose field 4 carries the text — which is where the client's reader looks
    /// for "successful"/"incorrect". One-byte lengths, which is all a sentence
    /// like this needs.
    /// </summary>
    static void WriteReply(NetworkStream stream, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var envelope = new byte[2 + data.Length];
        envelope[0] = 0x22; // field 4, wire type 2
        envelope[1] = (byte)data.Length;
        data.CopyTo(envelope, 2);

        var frame = new byte[8 + envelope.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, FrameMagic);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)envelope.Length);
        envelope.CopyTo(frame, 8);

        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }

    // ------------------------------------------------------------- the request
    //
    // Envelope: field 1 = header (the sequence and the session), field 4 = the
    // request. Request: field 1 = messageId (-1), field 3 = requestType (1 for
    // the password frame, absent for a command), field 4 = the text, field 5 =
    // the command's own line (the map, for bridge_setmode).

    static bool TryParseRequest(byte[] envelope, out int type, out string msg, out string extra)
    {
        type = 0;
        msg = "";
        extra = "";
        if (!TryTakeField(envelope, want: 4, out var inner))
            return false;

        var i = 0;
        while (i < inner.Length)
        {
            if (!TryReadVarint(inner, ref i, out var key))
                return false;
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);

            if (wire == 0)
            {
                if (!TryReadVarint(inner, ref i, out var v))
                    return false;
                if (field == 3)
                    type = (int)v;
                continue;
            }

            if (wire != 2)
                return false; // the client writes nothing else; do not guess
            if (!TryReadVarint(inner, ref i, out var n) || i + (int)n > inner.Length)
                return false;

            var text = Encoding.UTF8.GetString(inner, i, (int)n);
            i += (int)n;
            if (field == 4)
                msg = text;
            else if (field == 5)
                extra = text;
        }

        return msg.Length > 0;
    }

    static bool TryTakeField(byte[] message, int want, out byte[] value)
    {
        value = Array.Empty<byte>();
        var i = 0;
        while (i < message.Length)
        {
            if (!TryReadVarint(message, ref i, out var key))
                return false;
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0)
            {
                if (!TryReadVarint(message, ref i, out _))
                    return false;
                continue;
            }
            if (wire != 2)
                return false;
            if (!TryReadVarint(message, ref i, out var n) || i + (int)n > message.Length)
                return false;
            if (field == want)
            {
                value = message.AsSpan(i, (int)n).ToArray();
                return true;
            }
            i += (int)n;
        }

        return false;
    }

    static bool TryReadVarint(byte[] src, ref int i, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (i < src.Length && shift <= 63)
        {
            var b = src[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
        }
        return false;
    }

    static byte[]? ReadExact(NetworkStream stream, int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = stream.Read(buf, off, count - off);
            if (n <= 0)
                return null;
            off += n;
        }
        return buf;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already closed */ }
        _accept.Join(2000);
        _cts.Dispose();
    }
}
