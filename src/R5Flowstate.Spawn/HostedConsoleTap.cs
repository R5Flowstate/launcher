using System.IO.Pipes;
using System.Text;

namespace R5Flowstate.Spawn;

/// <summary>
/// Parent end of a local console tap. Child stdout -> R5F_CONSOLE_PIPE,
/// commands -> R5F_CONSOLE_IN (becomes the child's stdin).
/// </summary>
public sealed class HostedConsoleTap : IDisposable
{
    public const string HostedEnv = "R5F_HOSTED_CONSOLE";
    public const string PipeEnv = "R5F_CONSOLE_PIPE";
    public const string InEnv = "R5F_CONSOLE_IN";
    public const string RoleEnv = "R5F_CONSOLE_ROLE";

    private readonly NamedPipeServerStream _outPipe;
    private readonly NamedPipeServerStream _inPipe;
    private readonly CancellationTokenSource _cts = new();
    private StreamWriter? _inWriter;
    private bool _disposed;

    public LaunchRole Role { get; }
    public string OutPipePath { get; }
    public string InPipePath { get; }
    public event Action<string>? LineReceived;

    private HostedConsoleTap(LaunchRole role, string outLeaf, string inLeaf)
    {
        Role = role;
        OutPipePath = @"\\.\pipe\" + outLeaf;
        InPipePath = @"\\.\pipe\" + inLeaf;
        _outPipe = new NamedPipeServerStream(
            outLeaf, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, inBufferSize: 65536, outBufferSize: 65536);
        _inPipe = new NamedPipeServerStream(
            inLeaf, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, inBufferSize: 4096, outBufferSize: 4096);
    }

    public static HostedConsoleTap Create(LaunchRole role)
    {
        var tag = role == LaunchRole.Dedicated ? "s" : "c";
        var id = Guid.NewGuid().ToString("N");
        return new HostedConsoleTap(role, "r5f-con-" + tag + "-" + id, "r5f-in-" + tag + "-" + id);
    }

    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostedEnv] = "1",
            [PipeEnv] = OutPipePath,
            [InEnv] = InPipePath,
            [RoleEnv] = Role == LaunchRole.Dedicated ? "s" : "c",
        };
    }

    /// <summary>
    /// Wipe inherited hosted-console env so a child that is not tapped cannot
    /// bind the other role's leftover pipe names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ClearedEnvironment()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HostedEnv] = "0",
            [PipeEnv] = "",
            [InEnv] = "",
            [RoleEnv] = "",
        };
    }

    /// <summary>
    /// The two pipes are accepted independently. Sharing one thread makes the
    /// stdin connect gate the output read, and a child that opens stdout alone
    /// then shows nothing at all.
    /// </summary>
    public void Start()
    {
        new Thread(AcceptAndRead) { IsBackground = true, Name = "r5f-console-out" }.Start();
        new Thread(AcceptCommands) { IsBackground = true, Name = "r5f-console-in" }.Start();
    }

    private void AcceptCommands()
    {
        try
        {
            _inPipe.WaitForConnection();
            if (_cts.IsCancellationRequested)
                return;
            _inWriter = new StreamWriter(_inPipe, Encoding.Default, 256, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
        }
        catch (IOException)
        {
            // peer closed
        }
        catch (ObjectDisposedException)
        {
            // tap torn down
        }
    }

    /// <summary>
    /// One line in, one command out. An embedded newline would queue a second
    /// command the caller never authorised, so it ends the line instead.
    /// </summary>
    public bool TryWriteCommand(string line)
    {
        var writer = _inWriter;
        if (writer is null || string.IsNullOrWhiteSpace(line))
            return false;

        var cut = line.AsSpan().IndexOfAny('\r', '\n');
        var one = (cut >= 0 ? line[..cut] : line).Trim();
        if (one.Length == 0 || one.Length > 512)
            return false;

        try
        {
            writer.Write(one);
            writer.Write('\n');
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AcceptAndRead()
    {
        try
        {
            _outPipe.WaitForConnection();

            using var reader = new StreamReader(
                _outPipe,
                Encoding.Default,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var acc = new StringBuilder();
            const int maxLine = 16 * 1024;
            var buf = new char[1024];
            while (!_cts.IsCancellationRequested)
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
                        if (acc.Length >= maxLine)
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
            // peer closed
        }
        catch (ObjectDisposedException)
        {
            // tap torn down
        }
    }

    /// <summary>Raw, escapes included: the console renders them as colour.</summary>
    private void Emit(string raw)
    {
        LineReceived?.Invoke(raw);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        try { _inWriter?.Dispose(); } catch { /* ignore */ }
        try { _outPipe.Dispose(); } catch { /* ignore */ }
        try { _inPipe.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();
    }

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
