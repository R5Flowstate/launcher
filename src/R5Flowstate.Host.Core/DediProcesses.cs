using System.Diagnostics;
using R5Flowstate.Spawn;

namespace R5Flowstate.Host;

/// <summary>
/// Dedi processes that belong to one install. Windows asks the OS for each
/// image path; under wine the image is the wine loader, so the supervisor's
/// pid files are the record.
/// </summary>
public static class DediProcesses
{
    public static string RunDir(string installPath) =>
        Path.Combine(HostConfig.HostDir(installPath), "run");

    public static string PidPath(string installPath, string instance) =>
        Path.Combine(RunDir(installPath), instance + ".pid");

    public static int CountUnder(string installPath)
    {
        if (OperatingSystem.IsWindows())
            return ProcessSpawner.CountImagesUnderRoot(LaunchRole.Dedicated, installPath);
        return LivePids(installPath).Count;
    }

    public static IReadOnlyDictionary<string, int> LivePids(string installPath)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var dir = RunDir(installPath);
        if (!Directory.Exists(dir))
            return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.pid"))
        {
            if (!int.TryParse(File.ReadAllText(file).Trim(), out var pid))
                continue;
            if (IsAlive(pid))
                result[Path.GetFileNameWithoutExtension(file)] = pid;
            else
                TryDelete(file);
        }
        return result;
    }

    public static void WritePid(string installPath, string instance, int pid)
    {
        Directory.CreateDirectory(RunDir(installPath));
        File.WriteAllText(PidPath(installPath, instance), pid.ToString());
    }

    public static void ClearPid(string installPath, string instance) =>
        TryDelete(PidPath(installPath, instance));

    public static int KillUnder(string installPath, string? instance = null)
    {
        var killed = 0;
        foreach (var (name, pid) in LivePids(installPath))
        {
            if (instance is not null && name != instance)
                continue;
            if (Kill(pid))
                killed++;
            ClearPid(installPath, name);
        }
        if (instance is null && OperatingSystem.IsWindows())
            killed += ProcessSpawner.KillRole(LaunchRole.Dedicated, installPath);
        return killed;
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    static bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
