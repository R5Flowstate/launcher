namespace R5Flowstate.Linux.Core;

/// <summary>
/// Minimal Linux port of Spawn/LaunchArgs builders without user32.dll.
/// Mirrors Windows flags so EA/auth semantics stay identical:
/// offline -offline +cl_onlineAuthEnable 0, online +cl_onlineAuthEnable 1.
/// </summary>
public static class LaunchArgBuilder
{
    public static List<string> BuildClient(
        bool dev, bool offlineNoAuth, bool forceOnline,
        string? language, string? extra,
        string? windowMode, int width, int height,
        string? map, string? password, string? connect,
        bool dropDev = false)
    {
        var t = new List<string> { "-nodiscord", "-allowmultiple" };
        if (dev) { t.Add("-devsdk"); t.Add("-dev"); }
        if (offlineNoAuth && !forceOnline)
        {
            if (!t.Contains("-offline")) t.Add("-offline");
            AddPair(t, "+cl_onlineAuthEnable", "0");
        }
        if (forceOnline)
        {
            t.RemoveAll(x => x.Equals("-offline", StringComparison.OrdinalIgnoreCase)
                || x.Equals("-noorigin", StringComparison.OrdinalIgnoreCase));
            StripPair(t, "+cl_onlineAuthEnable");
            AddPair(t, "+cl_onlineAuthEnable", "1");
        }
        if (dropDev)
            t.RemoveAll(x => x.Equals("-dev", StringComparison.OrdinalIgnoreCase)
                || x.Equals("-developer", StringComparison.OrdinalIgnoreCase)
                || x.Equals("-devsdk", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(language))
        {
            t.Add("-language"); t.Add(language.Trim());
        }
        if (!string.IsNullOrWhiteSpace(extra))
            t.AddRange(ProtonLauncher.SplitArgs(extra));
        // miles last-wins
        t.RemoveAll(x => x.Equals("+miles_language", StringComparison.OrdinalIgnoreCase));
        // remove value following miles if present (best effort)
        AddPair(t, "+miles_language", "english");

        if (!string.IsNullOrWhiteSpace(windowMode))
        {
            switch (windowMode.Trim().ToLowerInvariant())
            {
                case "windowed": t.Add("-windowed"); break;
                case "borderless": t.Add("-windowed"); t.Add("-noborder"); break;
                case "fullscreen": t.Add("-fullscreen"); break;
            }
        }
        if (width > 0 && height > 0)
        {
            StripPair(t, "-width"); StripPair(t, "-height");
            AddPair(t, "-width", width.ToString());
            AddPair(t, "-height", height.ToString());
        }
        if (!string.IsNullOrWhiteSpace(map)) AddPair(t, "+map", map.Trim());
        if (!string.IsNullOrWhiteSpace(password)) AddPair(t, "+bridge_connect_password", password.Trim());
        if (!string.IsNullOrWhiteSpace(connect)) AddPair(t, "+connect", connect.Trim());
        return t;
    }

    public static List<string> BuildDedi(
        bool dev, bool offlineNoAuth, bool hostOnline,
        int port, string? playlist, string? map,
        string? password, bool cheats, string? extra)
    {
        var t = new List<string>
        {
            "-dedicated", "-port", port.ToString(),
            "+sv_allowSendTableTransmitToClients", "1",
            "+stringtable_compress", "1",
        };
        if (dev) { t.Add("-devsdk"); t.Add("-dev"); }
        bool hosting = hostOnline;
        if (offlineNoAuth && !hosting)
        {
            t.Add("-offline");
            AddPair(t, "+sv_onlineAuthEnable", "0");
        }
        AddPair(t, "+spire_host_visibility", hosting ? "2" : "0");
        if (cheats) AddPair(t, "+sv_cheats", "1");
        if (!string.IsNullOrWhiteSpace(password)) AddPair(t, "+sv_password", password.Trim());
        if (!string.IsNullOrWhiteSpace(playlist)) AddPair(t, "+launchplaylist", playlist.Trim());
        if (!string.IsNullOrWhiteSpace(map)) AddPair(t, "+map", map.Trim());
        if (!string.IsNullOrWhiteSpace(extra))
            t.AddRange(ProtonLauncher.SplitArgs(extra));
        return t;
    }

    private static void AddPair(List<string> t, string k, string v) { t.Add(k); t.Add(v); }
    private static void StripPair(List<string> t, string k)
    {
        for (int i = t.Count - 2; i >= 0; i--)
            if (t[i].Equals(k, StringComparison.OrdinalIgnoreCase)) { t.RemoveAt(i + 1); t.RemoveAt(i); }
    }
}
