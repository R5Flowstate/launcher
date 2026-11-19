using System.IO;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

/// <summary>
/// --build s21|s30 opens the launcher on that game build. It only picks between the folders the launcher already
/// remembers. A second start hands the request to the running instance through the stash file.
/// </summary>
public static class GameBuildLink
{
    const string Flag = "--build";

    static string PendingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "R5Flowstate", "pending_build.txt");

    public static string Argument(GameBuild build) => $"{Flag} {Name(build)}";

    public static bool TryParse(string[] args, out GameBuild build)
    {
        build = GameBuild.S21;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase))
                return TryParseName(args[i + 1], out build);
        }
        return false;
    }

    public static void StashPending(GameBuild build)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.WriteAllText(PendingPath, Name(build));
        }
        catch { }
    }

    public static bool TakePending(out GameBuild build)
    {
        build = GameBuild.S21;
        try
        {
            if (!File.Exists(PendingPath))
                return false;
            var name = File.ReadAllText(PendingPath).Trim();
            File.Delete(PendingPath);
            return TryParseName(name, out build);
        }
        catch
        {
            return false;
        }
    }

    static string Name(GameBuild build) => build == GameBuild.S30 ? "s30" : "s21";

    static bool TryParseName(string name, out GameBuild build)
    {
        build = GameBuild.S21;
        if (string.Equals(name, "s30", StringComparison.OrdinalIgnoreCase))
        {
            build = GameBuild.S30;
            return true;
        }
        return string.Equals(name, "s21", StringComparison.OrdinalIgnoreCase);
    }
}
