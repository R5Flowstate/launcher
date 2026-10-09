namespace R5Flowstate.Host;

public enum HostRing
{
    Live,
    Playtest,
}

public static class HostRings
{
    public const string CdnBase = "https://cdn.r5flowstate.org";

    public static string DedicatedChannelUrl(HostRing ring) =>
        CdnBase + "/channel/dedicated/" + Name(ring) + ".json";

    public static string Name(HostRing ring) => ring == HostRing.Playtest ? "playtest" : "live";

    public static bool TryParse(string? text, out HostRing ring)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "live":
                ring = HostRing.Live;
                return true;
            case "playtest":
                ring = HostRing.Playtest;
                return true;
            default:
                ring = HostRing.Live;
                return false;
        }
    }
}
