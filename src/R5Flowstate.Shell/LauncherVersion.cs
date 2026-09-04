using System.Reflection;

namespace R5Flowstate.Shell;

/// <summary>Same string as csproj Version / vpk --packVersion.</summary>
public static class LauncherVersion
{
    public static string Display { get; } = Resolve();

    static string Resolve()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            info = asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(info))
            return "dev";

        var plus = info.IndexOf('+');
        if (plus > 0)
            info = info[..plus];
        return info.Trim();
    }
}
