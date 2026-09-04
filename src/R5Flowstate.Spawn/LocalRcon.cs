using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace R5Flowstate.Spawn;

/// <summary>
/// Loopback netcon client for a launcher-spawned dedi. Speaks plaintext
/// envelopes (rcon_encryptframes 0) on [::1]. Password never goes on argv.
/// </summary>
public sealed class LocalRconSession : IDisposable
{
    public const string FromLauncherEnv = "FROM_R5F_LAUNCHER";
    public const string PasswordEnv = "R5F_LOCAL_RCON";
    public const string PortEnv = "R5F_LOCAL_RCON_PORT";
    public const int DefaultPort = 37017;
    public const int MinPasswordChars = 8;

    public int Port { get; }
    public string Host { get; } = "::1";

    private string _password;
    private bool _disposed;

    private LocalRconSession(string password, int port)
    {
        _password = password;
        Port = port;
    }

    public static LocalRconSession Create(int gamePort)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var password = Convert.ToHexString(bytes).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(bytes);
        var port = gamePort is > 0 and < 65534 ? gamePort + 2 : DefaultPort;
        if (port == 37016)
            port = DefaultPort;
        return new LocalRconSession(password, port);
    }

    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FromLauncherEnv] = "1",
            [PasswordEnv] = _password,
            [PortEnv] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public LocalRconResult SetMode(string playlist, string map, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!LocalRcon.TryBuildSetMode(playlist, map, out var line, out var err))
            return LocalRconResult.Fail(err);

        var budget = timeout ?? TimeSpan.FromSeconds(8);
        var deadline = DateTime.UtcNow + budget;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var one = NetconPlaintext.Exec(
                Host,
                Port,
                _password,
                "bridge_setmode",
                line,
                TimeSpan.FromSeconds(2));
            if (one.Ok)
                return one;
            last = one.Error;
            Thread.Sleep(400);
        }

        return LocalRconResult.Fail(last ?? "rcon not ready");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _password = string.Empty;
    }

    public override string ToString() => $"loopback rcon :{Port}";
}

public readonly struct LocalRconResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public static LocalRconResult Pass() => new() { Ok = true };
    public static LocalRconResult Fail(string? error) => new() { Ok = false, Error = error };
}

public static class LocalRcon
{
    public static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (var c in value)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                continue;
            return false;
        }
        return true;
    }

    public static bool TryBuildSetMode(string? playlist, string? map, out string command, out string? error)
    {
        command = string.Empty;
        error = null;
        var pl = (playlist ?? string.Empty).Trim();
        var mp = (map ?? string.Empty).Trim();
        if (!IsSafeIdentifier(pl))
        {
            error = "playlist is not a valid id";
            return false;
        }
        if (!IsSafeIdentifier(mp))
        {
            error = "map is not a valid id";
            return false;
        }
        command = "bridge_setmode " + pl + " " + mp;
        return true;
    }

    public static string? SelfCheck()
    {
        if (IsSafeIdentifier("survival_dev") == false)
            return "safe id rejected survival_dev";
        if (IsSafeIdentifier("mp_rr_desertlands_mu3") == false)
            return "safe id rejected map";
        if (IsSafeIdentifier("mp_rr_x;quit"))
            return "safe id accepted injection";
        if (IsSafeIdentifier("MP_RR_X"))
            return "safe id accepted uppercase";
        if (TryBuildSetMode("survival_dev", "mp_rr_desertlands_mu3", out var cmd, out _))
        {
            if (cmd != "bridge_setmode survival_dev mp_rr_desertlands_mu3")
                return "build mismatch: " + cmd;
        }
        else
            return "build rejected good pair";

        if (TryBuildSetMode("survival_dev", "mp_rr_x;quit", out _, out _))
            return "build accepted injection";

        var proto = NetconPlaintext.SelfCheck();
        if (proto is not null)
            return proto;
        return null;
    }
}

internal static class NetconPlaintext
{
    // Host-order matches INetCon.h RCON_FRAME_MAGIC. Wire is this value BE
    // (htonl), bytes 6E 6F 43 52 -- not ASCII "RCon" (52 43 6F 6E).
    private const uint FrameMagic = 0x6E6F4352;

    public static LocalRconResult Exec(
        string host,
        int port,
        string password,
        string verb,
        string fullLine,
        TimeSpan timeout)
    {
        try
        {
            using var client = ConnectLoopback(port, timeout);
            if (client is null)
                return LocalRconResult.Fail("connect timeout");

            using var stream = client.GetStream();
            stream.ReadTimeout = (int)Math.Clamp(timeout.TotalMilliseconds, 250, 8000);
            stream.WriteTimeout = stream.ReadTimeout;

            ulong sendSeq = 1;
            var session = (ulong)RandomNumberGenerator.GetInt32(int.MaxValue - 1) + 1UL;
            session |= (ulong)(uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) << 32;

            WriteFrame(stream, sendSeq++, session, BuildRequest(password, requestType: 1, extra: ""));
            if (!ReadAuthOk(stream))
                return LocalRconResult.Fail("auth refused");

            WriteFrame(stream, sendSeq, session, BuildRequest(verb, requestType: 0, extra: fullLine));
            return LocalRconResult.Pass();
        }
        catch (SocketException ex)
        {
            return LocalRconResult.Fail("connect " + ex.SocketErrorCode);
        }
        catch (IOException ex)
        {
            return LocalRconResult.Fail("io " + ex.Message);
        }
        catch (Exception ex)
        {
            return LocalRconResult.Fail(ex.GetType().Name);
        }
    }

    private static TcpClient? ConnectLoopback(int port, TimeSpan timeout)
    {
        try
        {
            var v6 = new TcpClient(AddressFamily.InterNetworkV6);
            v6.Client.DualMode = false;
            var t = v6.ConnectAsync(IPAddress.IPv6Loopback, port);
            if (t.Wait(timeout))
            {
                t.GetAwaiter().GetResult();
                return v6;
            }
            v6.Dispose();
        }
        catch
        {
            // try IPv4
        }

        try
        {
            var v4 = new TcpClient(AddressFamily.InterNetwork);
            var t = v4.ConnectAsync(IPAddress.Loopback, port);
            if (t.Wait(timeout))
            {
                t.GetAwaiter().GetResult();
                return v4;
            }
            v4.Dispose();
        }
        catch
        {
            // both failed
        }

        return null;
    }

    public static string? SelfCheck()
    {
        var req = BuildRequest("secret", requestType: 1, extra: "");
        if (req.Length < 10)
            return "request too small";
        if (req[0] != 0x08)
            return "request missing messageId";

        var env = BuildEnvelope(1, 0x11, req);
        if (env[0] != 0x0A)
            return "envelope missing header";

        var frame = BuildFrame(env);
        if (frame.Length < 8 + env.Length)
            return "frame short";
        if (frame[0] != 0x6E || frame[1] != 0x6F || frame[2] != 0x43 || frame[3] != 0x52)
            return "magic wire";
        if (BinaryPrimitives.ReadUInt32BigEndian(frame) != FrameMagic)
            return "magic endian";
        if (BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4)) != (uint)env.Length)
            return "length endian";
        return null;
    }

    private static byte[] BuildRequest(string requestMsg, int requestType, string extra)
    {
        var body = new List<byte>(64 + requestMsg.Length + extra.Length);
        WriteInt32(body, field: 1, -1);
        if (requestType != 0)
            WriteInt32(body, field: 3, requestType);
        WriteString(body, field: 4, requestMsg);
        if (!string.IsNullOrEmpty(extra))
            WriteString(body, field: 5, extra);
        return body.ToArray();
    }

    private static byte[] BuildEnvelope(ulong seq, ulong session, byte[] inner)
    {
        var header = new List<byte>(24);
        WriteUInt64(header, field: 1, seq);
        WriteUInt64(header, field: 2, session);

        var env = new List<byte>(32 + inner.Length);
        WriteBytes(env, field: 1, header.ToArray());
        WriteBytes(env, field: 4, inner);
        return env.ToArray();
    }

    private static byte[] BuildFrame(byte[] envelope)
    {
        var frame = new byte[8 + envelope.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, FrameMagic);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)envelope.Length);
        envelope.CopyTo(frame, 8);
        return frame;
    }

    private static void WriteFrame(NetworkStream stream, ulong seq, ulong session, byte[] request)
    {
        var bytes = BuildFrame(BuildEnvelope(seq, session, request));
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static bool ReadAuthOk(NetworkStream stream)
    {
        var header = new byte[8];
        if (!ReadExact(stream, header))
            return false;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic != FrameMagic)
            return false;
        var len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (len == 0 || len > 4096)
            return false;
        var payload = new byte[len];
        if (!ReadExact(stream, payload))
            return false;

        if (!TryExtractData(payload, out var data))
            return false;

        var msg = Encoding.UTF8.GetString(data);
        if (msg.Contains("incorrect", StringComparison.OrdinalIgnoreCase))
            return false;
        if (msg.Contains("successful", StringComparison.OrdinalIgnoreCase))
            return true;
        return data.Length > 0;
    }

    private static bool TryExtractData(byte[] envelope, out byte[] data)
    {
        data = Array.Empty<byte>();
        var i = 0;
        while (i < envelope.Length)
        {
            if (!TryReadVarint(envelope, ref i, out var key))
                return false;
            var field = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0)
            {
                if (!TryReadVarint(envelope, ref i, out _))
                    return false;
            }
            else if (wire == 2)
            {
                if (!TryReadVarint(envelope, ref i, out var n) || i + (int)n > envelope.Length)
                    return false;
                if (field == 4)
                {
                    data = envelope.AsSpan(i, (int)n).ToArray();
                    return true;
                }
                i += (int)n;
            }
            else
            {
                return false;
            }
        }
        return false;
    }

    private static bool ReadExact(NetworkStream stream, byte[] dest)
    {
        var off = 0;
        while (off < dest.Length)
        {
            var n = stream.Read(dest, off, dest.Length - off);
            if (n <= 0)
                return false;
            off += n;
        }
        return true;
    }

    private static void WriteInt32(List<byte> buf, int field, int value)
    {
        WriteVarint(buf, (ulong)((field << 3) | 0));
        WriteVarint(buf, unchecked((ulong)(long)value));
    }

    private static void WriteUInt64(List<byte> buf, int field, ulong value)
    {
        if (value == 0)
            return;
        WriteVarint(buf, (ulong)((field << 3) | 0));
        WriteVarint(buf, value);
    }

    private static void WriteString(List<byte> buf, int field, string value) =>
        WriteBytes(buf, field, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(List<byte> buf, int field, byte[] value)
    {
        WriteVarint(buf, (ulong)((field << 3) | 2));
        WriteVarint(buf, (ulong)value.Length);
        buf.AddRange(value);
    }

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)(value | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    private static bool TryReadVarint(byte[] src, ref int i, out ulong value)
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
}
