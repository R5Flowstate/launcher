namespace R5Flowstate.Host;

/// <summary>
/// platform/cfg/user/instance_&lt;name&gt;.cfg, exec'd on the dedi command line so
/// passwords and keys never appear in the process list. The reconciler treats
/// platform/cfg/user as host-owned and never deletes it.
/// </summary>
public static class InstanceCfg
{
    public const string RelativeDir = "platform/cfg/user";

    public sealed class Secrets
    {
        public string? ServerPassword { get; set; }
        public string? RconPassword { get; set; }
        public string? StatsHostKey { get; set; }
    }

    public static string PathFor(string installPath, HostInstance instance) =>
        Path.Combine(installPath, "platform", "cfg", "user", instance.CfgStem + ".cfg");

    public static string ExecArg(HostInstance instance) => "user/" + instance.CfgStem;

    /// <summary>Printable ASCII without quotes or command separators; 1..max.</summary>
    public static bool IsSafeValue(string? value, int max = 128) =>
        !string.IsNullOrEmpty(value) && value.Length <= max &&
        value.All(c => c >= 0x20 && c < 0x7F && c is not '"' and not ';' and not '\\');

    public static void Write(string installPath, HostInstance instance, Secrets secrets)
    {
        var lines = new List<string> { "// written by r5f-host; edits are overwritten" };
        Add(lines, "hostname", instance.Hostname);
        Add(lines, "hostdesc", instance.Description, 256);
        Add(lines, "sv_password", secrets.ServerPassword);
        Add(lines, "rcon_password", secrets.RconPassword);
        Add(lines, "fs_stats_host_key", secrets.StatsHostKey);

        var path = PathFor(installPath, instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join("\n", lines) + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads back what Write produced so a partial edit keeps the other secrets.</summary>
    public static Secrets Read(string installPath, HostInstance instance)
    {
        var s = new Secrets();
        var path = PathFor(installPath, instance);
        if (!File.Exists(path))
            return s;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var sp = line.IndexOf(' ');
            if (sp <= 0 || !line.EndsWith('"'))
                continue;
            var name = line[..sp];
            var value = line[(sp + 1)..].Trim().Trim('"');
            switch (name)
            {
                case "sv_password": s.ServerPassword = value; break;
                case "rcon_password": s.RconPassword = value; break;
                case "fs_stats_host_key": s.StatsHostKey = value; break;
            }
        }
        return s;
    }

    public static string NewRconPassword()
    {
        Span<byte> b = stackalloc byte[18];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).Replace('+', 'A').Replace('/', 'B');
    }

    static void Add(List<string> lines, string name, string? value, int max = 128)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (!IsSafeValue(value, max))
            throw new ArgumentException(name + " has characters the console cannot carry (quote, ';' or backslash).");
        lines.Add(name + " \"" + value + "\"");
    }
}
