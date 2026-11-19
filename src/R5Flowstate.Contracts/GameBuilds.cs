namespace R5Flowstate.Contracts;

public enum GameBuild
{
    S21,
    S30,
}

/// <summary>Which game an install folder holds, read off the folder itself.</summary>
public static class GameBuilds
{
    /// <summary>The S30.1 client worker; only an S30.1 install carries it.</summary>
    public const string S30ClientWorkerDll = "client_s30.dll";

    /// <summary>Written when an S30.1 download starts, so an unfinished folder still reads as S30.1.</summary>
    const string S30MarkerFile = "GAME_BUILD_S30";

    public static GameBuild Detect(string? installPath) =>
        HasS30Worker(installPath) || HasS30Marker(installPath) ? GameBuild.S30 : GameBuild.S21;

    public static bool IsS30(string? installPath) => Detect(installPath) == GameBuild.S30;

    public static bool HasS30Worker(string? installPath) => ExistsUnder(installPath, S30ClientWorkerDll);

    public static void MarkS30(string installPath)
    {
        var dir = Path.Combine(installPath.Trim(), ProductConstants.ContentCacheDirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, S30MarkerFile), string.Empty);
    }

    static bool HasS30Marker(string? installPath) =>
        ExistsUnder(installPath, Path.Combine(ProductConstants.ContentCacheDirName, S30MarkerFile));

    static bool ExistsUnder(string? installPath, string relative)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return false;
        try
        {
            return File.Exists(Path.Combine(installPath.Trim(), relative));
        }
        catch
        {
            return false;
        }
    }
}
