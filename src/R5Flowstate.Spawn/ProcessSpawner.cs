using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace R5Flowstate.Spawn;

public enum LaunchRole
{
    Client,
    Dedicated,
}

public enum WaitReadyResult
{
    Ready,
    ProcessDied,
    Timeout,

    /// <summary>
    /// UDP bind probe inconclusive; used short delay fallback while dedi still alive.
    /// Documented fallback for environments where bind semantics differ.
    /// </summary>
    DelayedFallback,
}

public sealed class PreflightResult
{
    public required bool Ok { get; init; }

    /// <summary>Absolute paths that failed the triad check (empty when Ok).</summary>
    public IReadOnlyList<string> MissingPaths { get; init; } = Array.Empty<string>();

    public string? FirstMissingPath => MissingPaths.Count > 0 ? MissingPaths[0] : null;

    /// <summary>First missing path (scaffold/shell alias).</summary>
    public string? MissingPath => FirstMissingPath;
    public string? ExePath { get; init; }
    public string? WorkerDll { get; init; }
    public string? LoaderDll { get; init; }
    public string? Root { get; init; }
}

public sealed class LaunchRequest
{
    public required string InstallRoot { get; init; }
    public required LaunchRole Role { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public bool CreateNoWindow { get; init; }
    public bool SetFromR5fLauncher { get; init; } = true;
    public IReadOnlyDictionary<string, string>? ExtraEnv { get; init; }

    /// <summary>Client only: run r5apex_dx12.exe instead of r5apex.exe.</summary>
    public bool UseDx12 { get; init; }
}

public sealed class LaunchResult
{
    public required bool Ok { get; init; }
    public int? ProcessId { get; init; }
    public Process? Process { get; init; }
    public string? ExePath { get; init; }
    public string? CommandLine { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Error { get; init; }
}

public sealed class PlayLocalOptions
{
    /// <summary>Colocated install root (s21-full style). Used when role roots are null.</summary>
    public required string InstallRoot { get; init; }

    /// <summary>Optional dual-root client path; defaults to InstallRoot.</summary>
    public string? ClientRoot { get; init; }

    /// <summary>Optional dual-root dedi path; defaults to InstallRoot.</summary>
    public string? DediRoot { get; init; }

    public LaunchProfile Profile { get; init; } = LaunchProfile.ShippingPlayer;
    public int Port { get; init; } = LaunchArgs.DefaultDediPort;
    public string ConnectHost { get; init; } = "127.0.0.1";
    public int WaitReadyMs { get; init; } = 8000;

    /// <summary>
    /// When UDP wait-ready times out but dedi is still alive, sleep this many ms
    /// then proceed (fallback). 0 = fail on timeout (strict).
    /// </summary>
    public int DelayFallbackMs { get; init; } = 2000;

    public string? DediMap { get; init; }
    public string? Password { get; init; }
    public string? ClientExtra { get; init; }
    public string? DediExtra { get; init; }
    public ClientWindowMode? ClientWindowMode { get; init; }
    public string? Language { get; init; }
}

public sealed class PlayLocalResult
{
    public required bool Ok { get; init; }
    public Process? DediProcess { get; init; }
    public int? DediPid { get; init; }
    public Process? ClientProcess { get; init; }
    public int? ClientPid { get; init; }
    public WaitReadyResult WaitReady { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> MissingPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// CreateProcess-style spawn: ApplicationName = abs EXE; CWD = role root;
/// env VPROJECT=1; optional FROM_R5F_LAUNCHER=1. Port of r5f_spawn contracts.
/// </summary>
public static class ProcessSpawner
{
    public const string ClientExeName = "r5apex.exe";
    public const string ClientDx12ExeName = "r5apex_dx12.exe";
    public const string DediExeName = "r5apex_ds.exe";

    /// <summary>
    /// Both client images. The renderer is chosen by WHICH EXE RUNS -- the SDK
    /// keys off "dx12" in the module name -- so process lookups have to cover
    /// both or a DX12 session looks like nothing is running.
    /// </summary>
    public static IReadOnlyList<string> ClientExeNames { get; } =
        new[] { ClientExeName, ClientDx12ExeName };

    /// <summary>Which renderer a live client is actually on, read off its image name.</summary>
    public enum ClientRenderer
    {
        None,
        Dx11,
        Dx12,
    }

    public static bool Dx12Available(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return false;
        try { return File.Exists(Path.Combine(Path.GetFullPath(installRoot), ClientDx12ExeName)); }
        catch { return false; }
    }
    public const string ClientWorkerDll = "client.dll";
    public const string ServerWorkerDll = "server.dll";
    public const string LoaderDllName = "loader.dll";

    private static readonly object s_trackLock = new();
    private static readonly List<TrackedProcess> s_tracked = new();

    // Liveness answers are polled several times per watchdog tick by different
    // UI paths; a short memo keeps that at one real enumeration per role.
    private const int LivenessMemoMs = 900;
    private sealed record LivenessMemo(bool Client, bool Dedi, ClientRenderer Renderer, DateTime Utc);
    private static readonly object s_livenessLock = new();
    private static LivenessMemo? s_liveness;
    private static string? s_livenessRoot;

    private static (bool client, bool dedi, ClientRenderer renderer)? ReadLivenessMemo(string? root)
    {
        lock (s_livenessLock)
        {
            if (s_liveness is { } m &&
                string.Equals(s_livenessRoot, root ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.UtcNow - m.Utc).TotalMilliseconds < LivenessMemoMs)
            {
                return (m.Client, m.Dedi, m.Renderer);
            }
            return null;
        }
    }

    private static void WriteLivenessMemo(string? root, bool client, bool dedi, ClientRenderer renderer)
    {
        lock (s_livenessLock)
        {
            s_livenessRoot = root ?? string.Empty;
            s_liveness = new LivenessMemo(client, dedi, renderer, DateTime.UtcNow);
        }
    }

    /// <summary>Drop the liveness memo after spawn/kill so the next read is real.</summary>
    public static void InvalidateLiveness() =>
        WriteLivenessMemo(null, false, false, ClientRenderer.None);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder text, ref uint size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Image path via QueryFullProcessImageName. Process.MainModule needs
    /// PROCESS_QUERY_INFORMATION + VM_READ and walks the whole module list --
    /// seconds against a protected game image -- while this is one syscall.
    /// </summary>
    private static string? QueryImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private sealed class TrackedProcess
    {
        public required LaunchRole Role { get; init; }
        public required Process Process { get; init; }
        public string? ExePath { get; init; }
    }

    /// <summary>
    /// Preflight triad for a role root: EXE + worker DLL + loader.dll.
    /// Returns all missing absolute paths (empty when Ok).
    /// Layout: files at root (s21-full) or under game/ (accepted fallback).
    /// </summary>
    public static PreflightResult PreflightRole(string root, LaunchRole role) =>
        PreflightRole(root, role, useDx12: false);

    public static PreflightResult PreflightRole(string root, LaunchRole role, bool useDx12)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return new PreflightResult
            {
                Ok = false,
                MissingPaths = new[] { "(empty root)" },
            };
        }

        string absRoot;
        try
        {
            absRoot = Path.GetFullPath(root);
        }
        catch (Exception)
        {
            return new PreflightResult
            {
                Ok = false,
                MissingPaths = new[] { root },
            };
        }

        var exeName = role == LaunchRole.Client
            ? (useDx12 ? ClientDx12ExeName : ClientExeName)
            : DediExeName;
        var workerName = role == LaunchRole.Client ? ClientWorkerDll : ServerWorkerDll;

        var exe = Path.Combine(absRoot, exeName);
        var worker = ResolveUnderRoot(absRoot, workerName);
        var loader = ResolveUnderRoot(absRoot, LoaderDllName);

        var missing = new List<string>();
        if (!File.Exists(exe))
            missing.Add(exe);
        if (worker is null || !File.Exists(worker))
            missing.Add(worker ?? Path.Combine(absRoot, workerName));
        if (loader is null || !File.Exists(loader))
            missing.Add(loader ?? Path.Combine(absRoot, LoaderDllName));

        return new PreflightResult
        {
            Ok = missing.Count == 0,
            MissingPaths = missing,
            ExePath = exe,
            WorkerDll = worker,
            LoaderDll = loader,
            Root = absRoot,
        };
    }

    /// <summary>Alias for PreflightRole (scaffold name).</summary>
    public static PreflightResult Preflight(string installRoot, LaunchRole role) =>
        PreflightRole(installRoot, role);

    /// <summary>
    /// Spawn with ProcessStartInfo: UseShellExecute=false, WorkingDirectory=cwd,
    /// env VPROJECT=1, optional FROM_R5F_LAUNCHER=1.
    /// </summary>
    public static LaunchResult SpawnProcess(
        string exePath,
        IReadOnlyList<string> args,
        string workingDirectory,
        bool setFromR5fLauncher = true,
        bool createNoWindow = false,
        LaunchRole? trackAs = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new LaunchResult
            {
                Ok = false,
                Error = "exe path empty",
            };
        }

        string absExe;
        string absCwd;
        try
        {
            absExe = Path.GetFullPath(exePath);
            absCwd = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(absExe) ?? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory);
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Ok = false,
                ExePath = exePath,
                Error = ex.Message,
            };
        }

        args ??= Array.Empty<string>();
        var cmdLine = LaunchArgs.FormatCommandLine(absExe, args);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = absExe,
                Arguments = LaunchArgs.FormatArgumentsOnly(args),
                WorkingDirectory = absCwd,
                UseShellExecute = false,
                CreateNoWindow = createNoWindow,
            };

            // Force VPROJECT=1 (loader cold-start contract).
            psi.Environment["VPROJECT"] = "1";
            if (setFromR5fLauncher)
                psi.Environment["FROM_R5F_LAUNCHER"] = "1";
            if (extraEnv is not null)
            {
                foreach (var kv in extraEnv)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value is null)
                        continue;
                    psi.Environment[kv.Key] = kv.Value;
                }
            }

            // When we host the console, the child still has to allocate a real
            // one -- the engine's logging writes through console APIs. It just
            // must never be seen, and the window AllocConsole creates inherits
            // this show state, so it comes up already hidden instead of being
            // hidden a few statements later and flashing on the way.
            if (psi.Environment.TryGetValue(HostedConsoleTap.HostedEnv, out var hosted)
                && hosted == "1")
                psi.WindowStyle = ProcessWindowStyle.Hidden;

            var proc = Process.Start(psi);
            if (proc is null)
            {
                return new LaunchResult
                {
                    Ok = false,
                    ExePath = absExe,
                    CommandLine = cmdLine,
                    WorkingDirectory = absCwd,
                    Error = "Process.Start returned null",
                };
            }

            Track(proc, trackAs, absExe);
            InvalidateLiveness();

            return new LaunchResult
            {
                Ok = true,
                ProcessId = proc.Id,
                Process = proc,
                ExePath = absExe,
                CommandLine = cmdLine,
                WorkingDirectory = absCwd,
            };
        }
        catch (Win32Exception ex)
        {
            return new LaunchResult
            {
                Ok = false,
                ExePath = absExe,
                CommandLine = cmdLine,
                WorkingDirectory = absCwd,
                Error = $"Win32 {ex.NativeErrorCode}: {ex.Message}",
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Ok = false,
                ExePath = absExe,
                CommandLine = cmdLine,
                WorkingDirectory = absCwd,
                Error = ex.Message,
            };
        }
    }

    /// <summary>Preflight then SpawnProcess for a role root (tracked per role).</summary>
    public static LaunchResult Spawn(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pre = PreflightRole(request.InstallRoot, request.Role, request.UseDx12);
        if (!pre.Ok)
        {
            return new LaunchResult
            {
                Ok = false,
                Error = $"Preflight failed; missing: {string.Join("; ", pre.MissingPaths)}",
                ExePath = pre.ExePath,
                WorkingDirectory = pre.Root,
            };
        }

        return SpawnProcess(
            pre.ExePath!,
            request.Args,
            pre.Root!,
            setFromR5fLauncher: request.SetFromR5fLauncher,
            createNoWindow: request.CreateNoWindow,
            trackAs: request.Role,
            extraEnv: request.ExtraEnv);
    }

    /// <summary>PIDs still tracked and alive for a role (best-effort).</summary>
    public static IReadOnlyList<int> GetTrackedPids(LaunchRole role)
    {
        lock (s_trackLock)
        {
            PruneLocked();
            return s_tracked
                .Where(t => t.Role == role)
                .Select(t =>
                {
                    try
                    {
                        if (t.Process.HasExited)
                            return -1;
                        return t.Process.Id;
                    }
                    catch { return -1; }
                })
                .Where(id => id > 0)
                .ToList();
        }
    }

    /// <summary>
    /// True if this role has a live tracked process, or a path-scoped image under installRoot.
    /// Memoized briefly: several UI surfaces poll this every watchdog beat.
    /// </summary>
    public static bool IsRoleAlive(LaunchRole role, string? installRoot = null)
    {
        if (GetTrackedPids(role).Count > 0)
            return true;

        if (ReadLivenessMemo(installRoot) is { } hit)
            return role == LaunchRole.Client ? hit.client : hit.dedi;

        var scan = ScanLiveness(installRoot);
        return role == LaunchRole.Client ? scan.client : scan.dedi;
    }

    /// <summary>
    /// Which client image is live under installRoot. The renderer is decided by
    /// WHICH EXE RUNS, so this -- not the launcher's DX12 checkbox -- is the only
    /// answer to "what is the running game actually rendering with".
    /// </summary>
    public static ClientRenderer LiveClientRenderer(string? installRoot)
    {
        foreach (var pid in GetTrackedPids(LaunchRole.Client))
        {
            var leaf = Path.GetFileName(QueryImagePath(pid) ?? string.Empty);
            if (string.Equals(leaf, ClientDx12ExeName, StringComparison.OrdinalIgnoreCase))
                return ClientRenderer.Dx12;
            if (string.Equals(leaf, ClientExeName, StringComparison.OrdinalIgnoreCase))
                return ClientRenderer.Dx11;
        }

        if (ReadLivenessMemo(installRoot) is { } hit)
            return hit.renderer;

        return ScanLiveness(installRoot).renderer;
    }

    private static (bool client, bool dedi, ClientRenderer renderer) ScanLiveness(string? installRoot)
    {
        var dx12 = CountImageUnderRoot(ClientDx12ExeName, installRoot) > 0;
        var dx11 = CountImageUnderRoot(ClientExeName, installRoot) > 0;
        var dedi = CountImageUnderRoot(DediExeName, installRoot) > 0;
        var renderer = dx12
            ? ClientRenderer.Dx12
            : dx11 ? ClientRenderer.Dx11 : ClientRenderer.None;
        WriteLivenessMemo(installRoot, dx12 || dx11, dedi, renderer);
        return (dx12 || dx11, dedi, renderer);
    }

    /// <summary>Count live role EXEs under installRoot (path-scoped).</summary>
    public static int CountImagesUnderRoot(LaunchRole role, string? installRoot)
    {
        // Both client images count: a DX12 session runs r5apex_dx12.exe and would
        // otherwise read as "nothing running" to every alive/kill check.
        var leaves = role == LaunchRole.Client ? ClientExeNames : new[] { DediExeName };
        var count = 0;
        foreach (var leaf in leaves)
            count += CountImageUnderRoot(leaf, installRoot);
        return count;
    }

    private static int CountImageUnderRoot(string leaf, string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return 0;

        string absRoot;
        try { absRoot = Path.GetFullPath(installRoot); }
        catch { return 0; }

        var count = 0;
        Process[] procs;
        try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(leaf)); }
        catch { return 0; }

        var rootPrefix = absRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited)
                    continue;
                string? path;
                try { path = QueryImagePath(p.Id); }
                catch { continue; }
                if (string.IsNullOrEmpty(path))
                    continue;
                var full = Path.GetFullPath(path);
                if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetDirectoryName(full), absRoot, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        return count;
    }

    /// <summary>
    /// Kill processes for one role: tracked first, then image-name under installRoot
    /// (path-scoped; never kills unrelated retail installs).
    /// </summary>
    public static int KillRole(LaunchRole role, string? installRoot = null)
    {
        var killed = 0;
        lock (s_trackLock)
        {
            PruneLocked();
            var hit = s_tracked.Where(t => t.Role == role).ToList();
            foreach (var t in hit)
            {
                if (TryKillProcess(t.Process))
                    killed++;
                s_tracked.Remove(t);
                try { t.Process.Dispose(); } catch { /* ignore */ }
            }
        }

        killed += KillByImageUnderRoot(role, installRoot);
        InvalidateLiveness();
        return killed;
    }

    /// <summary>Kill all tracked client + dedi processes (and path-scoped images if root set).</summary>
    public static int KillAll(string? installRoot = null)
    {
        var n = KillRole(LaunchRole.Client, installRoot);
        n += KillRole(LaunchRole.Dedicated, installRoot);
        return n;
    }

    /// <summary>
    /// Kill EVERY live dedi image, ignoring installRoot. Play Local owns the UDP
    /// port outright, and a crashed dedi from another deploy root holds it just
    /// as hard as one from ours -- path-scoped kills walk straight past it.
    /// </summary>
    public static int KillAllDediImages()
    {
        var killed = 0;
        lock (s_trackLock)
        {
            PruneLocked();
            foreach (var t in s_tracked.Where(t => t.Role == LaunchRole.Dedicated).ToList())
            {
                if (TryKillProcess(t.Process))
                    killed++;
                s_tracked.Remove(t);
                try { t.Process.Dispose(); } catch { /* ignore */ }
            }
        }

        Process[] procs;
        try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DediExeName)); }
        catch { procs = Array.Empty<Process>(); }

        foreach (var p in procs)
        {
            try
            {
                if (TryKillProcess(p))
                    killed++;
            }
            catch { /* ignore */ }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        InvalidateLiveness();
        return killed;
    }

    /// <summary>
    /// Poll until nothing holds the UDP port. Inverse of <see cref="WaitReadyDedi"/>:
    /// a successful bind means the port is free for the dedi we are about to spawn.
    /// </summary>
    public static bool WaitPortFree(int port, int timeoutMs)
    {
        if (port <= 0)
            return true;

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                udp.Bind(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                ex.SocketErrorCode == SocketError.AccessDenied)
            {
                if (Environment.TickCount64 >= deadline)
                    return false;
            }
            catch (SocketException)
            {
                return true;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Poll until dedi holds UDP port (bind fails with AddressAlreadyInUse) or process dies / timeout.
    /// Same contract as R5F_WaitReadyDedi.
    /// </summary>
    public static WaitReadyResult WaitReadyDedi(Process dediProc, int port, int timeoutMs)
    {
        if (dediProc is null || port <= 0)
            return WaitReadyResult.Timeout;

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (Environment.TickCount64 <= deadline)
        {
            try
            {
                if (dediProc.HasExited)
                    return WaitReadyResult.ProcessDied;
            }
            catch (InvalidOperationException)
            {
                return WaitReadyResult.ProcessDied;
            }

            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                udp.Bind(new IPEndPoint(IPAddress.Any, port));
                // Bind succeeded => nothing listening yet.
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                ex.SocketErrorCode == SocketError.AccessDenied)
            {
                // AddressAlreadyInUse: dedi holds the port.
                // AccessDenied: sometimes elevated exclusive bind; treat as occupied.
                return WaitReadyResult.Ready;
            }
            catch (SocketException)
            {
                // other socket noise: keep polling
            }

            Thread.Sleep(100);
        }

        try
        {
            if (dediProc.HasExited)
                return WaitReadyResult.ProcessDied;
        }
        catch (InvalidOperationException)
        {
            return WaitReadyResult.ProcessDied;
        }

        return WaitReadyResult.Timeout;
    }

    /// <summary>
    /// Play Local: preflight both -&gt; spawn dedi (no +connect) -&gt; UDP wait-ready -&gt;
    /// client +connect host:port. Client spawn failure leaves dedi running.
    /// </summary>
    public static PlayLocalResult PlayLocal(PlayLocalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var clientRoot = string.IsNullOrWhiteSpace(options.ClientRoot)
            ? options.InstallRoot
            : options.ClientRoot;
        var dediRoot = string.IsNullOrWhiteSpace(options.DediRoot)
            ? options.InstallRoot
            : options.DediRoot;

        var dediPre = PreflightRole(dediRoot, LaunchRole.Dedicated);
        var clientPre = PreflightRole(clientRoot, LaunchRole.Client);
        if (!dediPre.Ok || !clientPre.Ok)
        {
            var missing = new List<string>();
            missing.AddRange(dediPre.MissingPaths);
            missing.AddRange(clientPre.MissingPaths);
            return new PlayLocalResult
            {
                Ok = false,
                MissingPaths = missing,
                Error = $"Preflight failed; missing: {string.Join("; ", missing)}",
            };
        }

        var dediArgs = LaunchArgs.BuildDediArgs(options.Profile, new DediArgOptions
        {
            Port = options.Port,
            Map = options.DediMap,
            Password = options.Password,
            Extra = options.DediExtra,
        });

        var dedi = SpawnProcess(
            dediPre.ExePath!,
            dediArgs,
            dediPre.Root!,
            setFromR5fLauncher: true,
            trackAs: LaunchRole.Dedicated);

        if (!dedi.Ok || dedi.Process is null)
        {
            return new PlayLocalResult
            {
                Ok = false,
                Error = dedi.Error ?? "dedi spawn failed",
                DediProcess = dedi.Process,
                DediPid = dedi.ProcessId,
            };
        }

        var ready = WaitReadyDedi(dedi.Process, options.Port, options.WaitReadyMs);
        if (ready == WaitReadyResult.ProcessDied)
        {
            return new PlayLocalResult
            {
                Ok = false,
                DediProcess = dedi.Process,
                DediPid = dedi.ProcessId,
                WaitReady = ready,
                Error = "dedi process died during wait-ready",
            };
        }

        if (ready == WaitReadyResult.Timeout)
        {
            // Documented fallback: short delay then proceed if process still alive.
            if (options.DelayFallbackMs > 0 && !dedi.Process.HasExited)
            {
                Thread.Sleep(options.DelayFallbackMs);
                if (dedi.Process.HasExited)
                {
                    return new PlayLocalResult
                    {
                        Ok = false,
                        DediProcess = dedi.Process,
                        DediPid = dedi.ProcessId,
                        WaitReady = WaitReadyResult.ProcessDied,
                        Error = "dedi process died during delay fallback",
                    };
                }

                ready = WaitReadyResult.DelayedFallback;
            }
            else
            {
                return new PlayLocalResult
                {
                    Ok = false,
                    DediProcess = dedi.Process,
                    DediPid = dedi.ProcessId,
                    WaitReady = WaitReadyResult.Timeout,
                    Error = $"dedi did not bind port {options.Port} within {options.WaitReadyMs} ms",
                };
            }
        }

        var clientArgs = LaunchArgs.BuildClientArgs(options.Profile, new ClientArgOptions
        {
            WindowMode = options.ClientWindowMode,
            IncludeConnect = true,
            ConnectHost = options.ConnectHost,
            ConnectPort = options.Port,
            Password = options.Password,
            Extra = options.ClientExtra,
            Language = options.Language,
        });

        var client = SpawnProcess(
            clientPre.ExePath!,
            clientArgs,
            clientPre.Root!,
            setFromR5fLauncher: true,
            trackAs: LaunchRole.Client);

        if (!client.Ok)
        {
            return new PlayLocalResult
            {
                Ok = false,
                DediProcess = dedi.Process,
                DediPid = dedi.ProcessId,
                ClientProcess = client.Process,
                ClientPid = client.ProcessId,
                WaitReady = ready,
                Error = "client spawn failed -- dedi left running: " + (client.Error ?? "unknown"),
            };
        }

        return new PlayLocalResult
        {
            Ok = true,
            DediProcess = dedi.Process,
            DediPid = dedi.ProcessId,
            ClientProcess = client.Process,
            ClientPid = client.ProcessId,
            WaitReady = ready,
        };
    }

    /// <summary>Terminate all tracked processes (both roles). Prefer KillAll(installRoot).</summary>
    public static void StopAll() => KillAll(installRoot: null);

    private static void Track(Process proc, LaunchRole? role, string? exePath)
    {
        lock (s_trackLock)
        {
            PruneLocked();
            s_tracked.Add(new TrackedProcess
            {
                Role = role ?? LaunchRole.Client,
                Process = proc,
                ExePath = exePath,
            });
        }
    }

    private static void PruneLocked()
    {
        for (var i = s_tracked.Count - 1; i >= 0; i--)
        {
            var t = s_tracked[i];
            try
            {
                if (t.Process.HasExited)
                {
                    try { t.Process.Dispose(); } catch { /* ignore */ }
                    s_tracked.RemoveAt(i);
                }
            }
            catch
            {
                s_tracked.RemoveAt(i);
            }
        }
    }

    private static bool TryKillProcess(Process p)
    {
        try
        {
            if (p.HasExited)
                return false;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Kill processes whose MainModule path is under installRoot and matches role exe name.
    /// Catches children not held in the tracked list.
    /// </summary>
    private static int KillByImageUnderRoot(LaunchRole role, string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
            return 0;

        string absRoot;
        try { absRoot = Path.GetFullPath(installRoot); }
        catch { return 0; }

        // Both client images, so STOP reaches a DX12 session too.
        var leaves = role == LaunchRole.Client ? ClientExeNames : new[] { DediExeName };
        var killed = 0;

        var procList = new List<Process>();
        foreach (var leaf in leaves)
        {
            try { procList.AddRange(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(leaf))); }
            catch { /* ignore this image */ }
        }

        var procs = procList.ToArray();
        foreach (var p in procs)
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch { /* access denied / 32-bit */ }

                if (string.IsNullOrEmpty(path))
                {
                    // Fallback: kill if CWD matches install root (weaker).
                    try
                    {
                        // no reliable CWD; skip unscoped kill
                    }
                    catch { /* ignore */ }
                    continue;
                }

                var full = Path.GetFullPath(path);
                if (!full.StartsWith(absRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetDirectoryName(full), absRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryKillProcess(p))
                    killed++;
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        return killed;
    }

    /// <summary>Prefer root\leaf; fall back to root\game\leaf (some tree layouts).</summary>
    private static string? ResolveUnderRoot(string root, string leaf)
    {
        var atRoot = Path.Combine(root, leaf);
        if (File.Exists(atRoot))
            return atRoot;

        var underGame = Path.Combine(root, "game", leaf);
        if (File.Exists(underGame))
            return underGame;

        // Prefer reporting root path as the expected missing location (s21-full layout).
        return atRoot;
    }
}
