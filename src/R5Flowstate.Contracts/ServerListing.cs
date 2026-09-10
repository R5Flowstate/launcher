using System.Text.Json;

namespace R5Flowstate.Contracts;

/// <summary>
/// 11-field listing from POST /spire/hosts. Wrong JSON types are dropped,
/// same as the game client.
/// </summary>
public sealed class ServerListing
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Hidden { get; init; }
    public string Map { get; init; } = string.Empty;
    public string Playlist { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Key { get; init; } = string.Empty;
    public uint Checksum { get; init; }
    public int NumPlayers { get; init; }
    public int MaxPlayers { get; init; }
    public bool HasPassword { get; init; }
    public string ModsProfile { get; init; } = string.Empty;
    public IReadOnlyList<string> RequiredMods { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedMods { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Frozen update-required notice: port 0, ip ::1, empty key/map, checksum 0.
    /// </summary>
    public bool IsUpdateNotice =>
        Port <= 0
        && Checksum == 0
        && string.IsNullOrEmpty(Key)
        && string.IsNullOrEmpty(Map)
        && (Ip == "::1" || Ip == "0:0:0:0:0:0:0:1");

    /// <summary>Loopback / filler / port-0 rows are not joinable public hosts.</summary>
    public bool IsSynthetic
    {
        get
        {
            var ip = Ip.Trim();
            return Hidden
                   || Port <= 0
                   || string.IsNullOrWhiteSpace(ip)
                   || ip == "::1"
                   || ip == "0:0:0:0:0:0:0:1"
                   || ip == "127.0.0.1"
                   || ip == "0.0.0.0"
                   || string.Equals(ip, "localhost", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(ip, "[::1]", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(ip, "::ffff:127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool CanJoin =>
        !IsSynthetic && Port is > 0 and < 65536;
}

public sealed class ServerListResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ServerListing> Servers { get; init; } = Array.Empty<ServerListing>();
    public IReadOnlyList<ServerListing> Dropped { get; init; } = Array.Empty<ServerListing>();
    public bool UpdateRequired { get; init; }
    public int RawCount { get; init; }

    public static ServerListResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public static class ServerListingParser
{
    public static ServerListResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ServerListResult.Fail("Empty master-server response.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ServerListResult.Fail("Master-server response was not JSON: " + ex.Message);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return ServerListResult.Fail("Master-server response was not an object.");

            var root = doc.RootElement;
            if (!TryGetBool(root, "success", out var success))
                return ServerListResult.Fail("Master-server response missing success.");

            if (!success)
            {
                var err = TryGetString(root, "error") ?? "Server list failed.";
                return ServerListResult.Fail(err);
            }

            if (!root.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return ServerListResult.Fail("Master-server response missing servers array.");

            var kept = new List<ServerListing>();
            var dropped = new List<ServerListing>();
            var notices = 0;
            var raw = 0;

            foreach (var el in arr.EnumerateArray())
            {
                raw++;
                if (!TryParseListing(el, out var row) || row is null)
                    continue;

                if (row.IsUpdateNotice)
                {
                    notices++;
                    dropped.Add(row);
                    continue;
                }

                if (row.IsSynthetic || row.Hidden)
                {
                    dropped.Add(row);
                    continue;
                }

                kept.Add(row);
            }

            kept.Sort((a, b) => b.NumPlayers.CompareTo(a.NumPlayers));

            return new ServerListResult
            {
                Success = true,
                Servers = kept,
                Dropped = dropped,
                RawCount = raw,
                UpdateRequired = kept.Count == 0 && notices > 0,
            };
        }
    }

    public static bool TryParseListing(JsonElement el, out ServerListing? listing)
    {
        listing = null;
        if (el.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetString(el, "name", out var name))
            return false;
        if (!TryGetString(el, "description", out var description))
            return false;
        if (!TryGetBool(el, "hidden", out var hidden))
            return false;
        if (!TryGetString(el, "map", out var map))
            return false;
        if (!TryGetString(el, "playlist", out var playlist))
            return false;
        if (!TryGetString(el, "ip", out var ip))
            return false;
        if (!TryGetInt(el, "port", out var port))
            return false;
        if (!TryGetString(el, "key", out var key))
            return false;
        if (!TryGetUInt(el, "checksum", out var checksum))
            return false;
        if (!TryGetInt(el, "numPlayers", out var numPlayers))
            return false;
        if (!TryGetInt(el, "maxPlayers", out var maxPlayers))
            return false;

        var hasPassword = false;
        if (el.TryGetProperty("hasPassword", out var pwEl)
            && (pwEl.ValueKind == JsonValueKind.True || pwEl.ValueKind == JsonValueKind.False))
            hasPassword = pwEl.GetBoolean();

        var modsProfile = string.Empty;
        if (el.TryGetProperty("modsProfile", out var modsEl) && modsEl.ValueKind == JsonValueKind.String)
            modsProfile = modsEl.GetString() ?? string.Empty;

        IReadOnlyList<string> requiredMods = Array.Empty<string>();
        if (el.TryGetProperty("requiredMods", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            var mods = new List<string>();
            foreach (var m in reqEl.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.String)
                {
                    var s = m.GetString();
                    if (!string.IsNullOrEmpty(s))
                        mods.Add(s);
                }
            }
            requiredMods = mods;
        }

        IReadOnlyList<string> allowedMods = Array.Empty<string>();
        if (el.TryGetProperty("allowedMods", out var allowEl) && allowEl.ValueKind == JsonValueKind.Array)
        {
            var mods = new List<string>();
            foreach (var m in allowEl.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.String)
                {
                    var s = m.GetString();
                    if (!string.IsNullOrEmpty(s))
                        mods.Add(s);
                }
            }
            allowedMods = mods;
        }

        listing = new ServerListing
        {
            Name = name,
            Description = description,
            Hidden = hidden,
            Map = map,
            Playlist = playlist,
            Ip = ip,
            Port = port,
            Key = key,
            Checksum = checksum,
            NumPlayers = numPlayers,
            MaxPlayers = maxPlayers,
            HasPassword = hasPassword,
            ModsProfile = modsProfile,
            RequiredMods = requiredMods,
            AllowedMods = allowedMods,
        };
        return true;
    }

    /// <summary>Parser fixtures. Returns null when every case passes.</summary>
    public static string? SelfCheck()
    {
        var ok = Parse("""
            {"success":true,"servers":[
              {"name":"Cafe","description":"x","hidden":false,"map":"mp_rr_arena_composite",
               "playlist":"fs_1v1","ip":"203.0.113.9","port":37015,"key":"WDNWLmJYQ2ZlM0VoTid3Yg==",
               "checksum":1,"numPlayers":3,"maxPlayers":12,"hasPassword":false}
            ]}
            """);
        if (!ok.Success || ok.Servers.Count != 1 || ok.Servers[0].NumPlayers != 3)
            return "keep real listing";

        var locked = Parse("""
            {"success":true,"servers":[
              {"name":"Locked","description":"x","hidden":false,"map":"mp_rr_arena_composite",
               "playlist":"fs_1v1","ip":"203.0.113.9","port":37015,"key":"WDNWLmJYQ2ZlM0VoTid3Yg==",
               "checksum":1,"numPlayers":2,"maxPlayers":12,"hasPassword":true}
            ]}
            """);
        if (!locked.Success || locked.Servers.Count != 1
            || !locked.Servers[0].HasPassword || !locked.Servers[0].CanJoin)
            return "passworded listing is joinable";

        var dropType = Parse("""
            {"success":true,"servers":[
              {"name":"bad","description":"x","hidden":false,"map":"m","playlist":"p",
               "ip":"203.0.113.9","port":"37015","key":"k","checksum":1,
               "numPlayers":1,"maxPlayers":12}
            ]}
            """);
        if (!dropType.Success || dropType.Servers.Count != 0)
            return "drop mistyped port";

        var notice = Parse("""
            {"success":true,"servers":[
              {"name":"Update","description":"d","hidden":false,"map":"","playlist":"update_required",
               "ip":"::1","port":0,"key":"","checksum":0,"numPlayers":0,"maxPlayers":0}
            ]}
            """);
        if (!notice.Success || !notice.UpdateRequired || notice.Servers.Count != 0)
            return "update-required notice";

        var empty = Parse("""{"success":true,"servers":[]}""");
        if (!empty.Success || empty.UpdateRequired || empty.Servers.Count != 0)
            return "empty allowed list is not update-required";

        var fail = Parse("""{"success":false,"error":"nope"}""");
        if (fail.Success || fail.Error != "nope")
            return "success false";

        return null;
    }

    private static bool TryGetString(JsonElement el, string name, out string value)
    {
        value = string.Empty;
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString() ?? string.Empty;
        return true;
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        return TryGetString(el, name, out var v) ? v : null;
    }

    private static bool TryGetBool(JsonElement el, string name, out bool value)
    {
        value = false;
        if (!el.TryGetProperty(name, out var p))
            return false;
        if (p.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (p.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    private static bool TryGetInt(JsonElement el, string name, out int value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
            return false;
        return p.TryGetInt32(out value);
    }

    private static bool TryGetUInt(JsonElement el, string name, out uint value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
            return false;
        if (p.TryGetUInt32(out value))
            return true;
        if (p.TryGetInt64(out var wide) && wide >= 0 && wide <= uint.MaxValue)
        {
            value = (uint)wide;
            return true;
        }
        return false;
    }
}
