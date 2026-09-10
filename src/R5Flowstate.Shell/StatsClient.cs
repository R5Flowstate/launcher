using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public sealed class LeaderboardResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StatsPlayer> Players { get; init; } = Array.Empty<StatsPlayer>();
    public StatsPagination Pagination { get; init; } = new();

    public static LeaderboardResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class PlayerStatsResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public StatsPlayer? Player { get; init; }
    public IReadOnlyList<StatsMatch> RecentMatches { get; init; } = Array.Empty<StatsMatch>();
    public IReadOnlyList<MapSplit> Maps { get; init; } = Array.Empty<MapSplit>();
    public IReadOnlyList<WeaponSplit> Weapons { get; init; } = Array.Empty<WeaponSplit>();

    public static PlayerStatsResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class MatchesResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StatsMatch> Matches { get; init; } = Array.Empty<StatsMatch>();
    public StatsPagination Pagination { get; init; } = new();

    public static MatchesResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class MatchSessionsResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StatsMatchSession> Matches { get; init; } = Array.Empty<StatsMatchSession>();
    public StatsPagination Pagination { get; init; } = new();

    public static MatchSessionsResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class MatchDetailResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public StatsMatchSession? Match { get; init; }

    public static MatchDetailResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class ActivityResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int Servers { get; init; }
    public int Players { get; init; }
    public int Capacity { get; init; }
    public IReadOnlyList<ActivityListing> Listings { get; init; } = Array.Empty<ActivityListing>();

    public static ActivityResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class StatsPlayer
{
    [JsonPropertyName("rank")]
    public int Rank { get; init; }

    [JsonPropertyName("accountId")]
    public long AccountId { get; init; }

    [JsonPropertyName("persona")]
    public string Persona { get; init; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; init; }

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; init; }

    [JsonPropertyName("kd")]
    public double Kd { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }

    [JsonPropertyName("accuracy")]
    public double Accuracy { get; init; }

    [JsonPropertyName("headshots")]
    public int Headshots { get; init; }

    [JsonPropertyName("hits")]
    public int Hits { get; init; }

    [JsonPropertyName("shots")]
    public int Shots { get; init; }

    [JsonPropertyName("wins")]
    public int Wins { get; init; }

    [JsonPropertyName("losses")]
    public int Losses { get; init; }

    [JsonPropertyName("games")]
    public int Games { get; init; }

    [JsonPropertyName("winRate")]
    public double WinRate { get; init; }

    [JsonPropertyName("timePlayed")]
    public int TimePlayed { get; init; }

    [JsonPropertyName("mostUsedWeapon")]
    public string? MostUsedWeapon { get; init; }

    [JsonPropertyName("mostUsedInput")]
    public string? MostUsedInput { get; init; }

    [JsonPropertyName("currentWinStreak")]
    public int CurrentWinStreak { get; init; }

    [JsonPropertyName("longestWinStreak")]
    public int LongestWinStreak { get; init; }

    [JsonPropertyName("mostKillsInMatch")]
    public int MostKillsInMatch { get; init; }

    [JsonPropertyName("mostDamageInMatch")]
    public int MostDamageInMatch { get; init; }
}

public sealed class MapSplit
{
    [JsonPropertyName("map")]
    public string Map { get; init; } = string.Empty;

    [JsonPropertyName("games")]
    public int Games { get; init; }

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }

    [JsonPropertyName("wins")]
    public int Wins { get; init; }
}

public sealed class WeaponSplit
{
    [JsonPropertyName("weapon")]
    public string Weapon { get; init; } = string.Empty;

    [JsonPropertyName("games")]
    public int Games { get; init; }

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }
}

public sealed class ActivityListing
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("map")]
    public string Map { get; init; } = string.Empty;

    [JsonPropertyName("playlist")]
    public string Playlist { get; init; } = string.Empty;

    [JsonPropertyName("numPlayers")]
    public int NumPlayers { get; init; }

    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; init; }

    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; init; }
}

public sealed class StatsMatch
{
    [JsonPropertyName("accountId")]
    public long AccountId { get; init; }

    [JsonPropertyName("persona")]
    public string Persona { get; init; } = string.Empty;

    [JsonPropertyName("opponentId")]
    public long? OpponentId { get; init; }

    [JsonPropertyName("opponentPersona")]
    public string? OpponentPersona { get; init; }

    [JsonPropertyName("map")]
    public string Map { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }

    [JsonPropertyName("at")]
    public string At { get; init; } = string.Empty;

    [JsonPropertyName("weapon")]
    public string? Weapon { get; init; }

    [JsonPropertyName("duration")]
    public int Duration { get; init; }

    [JsonPropertyName("hostName")]
    public string? HostName { get; init; }

    [JsonPropertyName("matchId")]
    public string? MatchId { get; init; }

    [JsonPropertyName("playlist")]
    public string? Playlist { get; init; }

    [JsonPropertyName("shots")]
    public int Shots { get; init; }

    [JsonPropertyName("hits")]
    public int Hits { get; init; }

    [JsonPropertyName("headshots")]
    public int Headshots { get; init; }

    [JsonPropertyName("accuracy")]
    public double Accuracy { get; init; }

    [JsonPropertyName("input")]
    public string? Input { get; init; }
}

public sealed class StatsMatchPlayer
{
    [JsonPropertyName("accountId")]
    public long AccountId { get; init; }

    [JsonPropertyName("persona")]
    public string? Persona { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }

    [JsonPropertyName("shots")]
    public int Shots { get; init; }

    [JsonPropertyName("hits")]
    public int Hits { get; init; }

    [JsonPropertyName("headshots")]
    public int Headshots { get; init; }

    [JsonPropertyName("accuracy")]
    public double Accuracy { get; init; }

    [JsonPropertyName("weapon")]
    public string? Weapon { get; init; }

    [JsonPropertyName("input")]
    public string? Input { get; init; }
}

public sealed class StatsMatchSession
{
    [JsonPropertyName("matchId")]
    public string MatchId { get; init; } = string.Empty;

    [JsonPropertyName("at")]
    public string At { get; init; } = string.Empty;

    [JsonPropertyName("map")]
    public string? Map { get; init; }

    [JsonPropertyName("playlist")]
    public string? Playlist { get; init; }

    [JsonPropertyName("duration")]
    public int Duration { get; init; }

    [JsonPropertyName("hostName")]
    public string? HostName { get; init; }

    [JsonPropertyName("players")]
    public StatsMatchPlayer[] Players { get; init; } = Array.Empty<StatsMatchPlayer>();

    [JsonPropertyName("winnerId")]
    public long? WinnerId { get; init; }

    [JsonPropertyName("winnerPersona")]
    public string? WinnerPersona { get; init; }

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("damage")]
    public int Damage { get; init; }
}

public sealed class StatsSeason
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("isAll")]
    public bool IsAll { get; init; }
}

public sealed class SeasonsResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<StatsSeason> Seasons { get; init; } = Array.Empty<StatsSeason>();
    public StatsSeason? Current { get; init; }
    public StatsSeason? All { get; init; }

    public static SeasonsResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public sealed class StatsPagination
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("hasNext")]
    public bool HasNext { get; init; }

    [JsonPropertyName("hasPrevious")]
    public bool HasPrevious { get; init; }
}

/// <summary>
/// Public 1v1 stats reads. Never ingest, mint, ops, or hosts/publish.
/// </summary>
public static class StatsClient
{
    public const int DefaultLimit = 50;

    private static readonly HttpClient s_http;
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> s_sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rank", "persona", "score", "kills", "deaths", "kd", "damage",
        "accuracy", "headshots", "hits", "shots", "wins", "losses",
        "games", "winRate", "timePlayed", "mostUsedWeapon", "mostUsedInput",
        "streak", "winStreak",
    };

    static StatsClient()
    {
        s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "R5FlowstateLauncher");
        s_http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json");
    }

    public static async Task<LeaderboardResult> GetLeaderboardAsync(
        string? baseUrl,
        string sort = "score",
        string order = "desc",
        int limit = DefaultLimit,
        int offset = 0,
        CancellationToken cancel = default,
        string? season = null,
        string? q = null)
    {
        sort = NormalizeSort(sort);
        order = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);

        var path =
            "/stats/1v1/leaderboard?sort=" + Uri.EscapeDataString(sort) +
            "&order=" + Uri.EscapeDataString(order) +
            "&limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(season))
            path += "&season=" + Uri.EscapeDataString(season.Trim());
        var qClean = SanitizeQ(q);
        if (qClean.Length > 0)
            path += "&q=" + Uri.EscapeDataString(qClean);

        var got = await GetPublicAsync(baseUrl, path, cancel).ConfigureAwait(false);
        if (!got.Ok)
            return LeaderboardResult.Fail(got.Error ?? "Leaderboard failed.");
        return ParseLeaderboard(got.Body);
    }

    public static async Task<SeasonsResult> GetSeasonsAsync(
        string? baseUrl,
        CancellationToken cancel = default)
    {
        var got = await GetPublicAsync(baseUrl, "/stats/1v1/seasons", cancel).ConfigureAwait(false);
        if (!got.Ok)
            return SeasonsResult.Fail(got.Error ?? "Seasons failed.");
        try
        {
            var wire = JsonSerializer.Deserialize<SeasonsWire>(got.Body, s_json);
            if (wire is null || wire.Success == false)
                return SeasonsResult.Fail(NonEmpty(wire?.Error, "Seasons failed."));
            return new SeasonsResult
            {
                Success = true,
                Seasons = wire.Seasons ?? Array.Empty<StatsSeason>(),
                Current = wire.Current,
                All = wire.All,
            };
        }
        catch (JsonException ex)
        {
            return SeasonsResult.Fail("Seasons response was not JSON: " + ex.Message);
        }
    }

    public static async Task<PlayerStatsResult> GetPlayerAsync(
        string? baseUrl,
        long accountId,
        CancellationToken cancel = default,
        string? season = null)
    {
        if (accountId < 0)
            return PlayerStatsResult.Fail("Invalid account id.");

        var path = "/stats/1v1/player/" +
                   accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(season))
            path += "?season=" + Uri.EscapeDataString(season.Trim());
        var got = await GetPublicAsync(baseUrl, path, cancel).ConfigureAwait(false);
        if (!got.Ok)
            return PlayerStatsResult.Fail(got.Error ?? "Player stats failed.");
        return ParsePlayer(got.Body);
    }

    public static async Task<MatchesResult> GetMatchesAsync(
        string? baseUrl,
        long? accountId = null,
        string? map = null,
        int limit = DefaultLimit,
        int offset = 0,
        CancellationToken cancel = default,
        string? season = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var path =
            "/stats/1v1/matches?limit=" +
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "&offset=" +
            offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (accountId is long id && id > 0)
            path += "&account_id=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(map))
            path += "&map=" + Uri.EscapeDataString(map.Trim());
        if (!string.IsNullOrWhiteSpace(season))
            path += "&season=" + Uri.EscapeDataString(season.Trim());

        var got = await GetPublicAsync(baseUrl, path, cancel).ConfigureAwait(false);
        if (!got.Ok)
            return MatchesResult.Fail(got.Error ?? "Matches failed.");
        return ParseMatches(got.Body);
    }

    public static async Task<MatchSessionsResult> GetMatchSessionsAsync(
        string? baseUrl,
        long? accountId = null,
        string? map = null,
        int limit = DefaultLimit,
        int offset = 0,
        CancellationToken cancel = default,
        string? season = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        var path =
            "/stats/1v1/matches?group=match&limit=" +
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "&offset=" +
            offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (accountId is long id && id > 0)
            path += "&account_id=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(map))
            path += "&map=" + Uri.EscapeDataString(map.Trim());
        if (!string.IsNullOrWhiteSpace(season))
            path += "&season=" + Uri.EscapeDataString(season.Trim());

        var got = await GetPublicAsync(baseUrl, path, cancel).ConfigureAwait(false);
        if (!got.Ok)
            return MatchSessionsResult.Fail(got.Error ?? "Matches failed.");
        return ParseMatchSessions(got.Body);
    }

    public static async Task<MatchDetailResult> GetMatchAsync(
        string? baseUrl,
        string? matchId,
        CancellationToken cancel = default)
    {
        var id = SanitizeMatchId(matchId);
        if (id.Length == 0)
            return MatchDetailResult.Fail("Invalid match id.");

        var got = await GetPublicAsync(baseUrl, "/stats/1v1/match/" + id, cancel)
            .ConfigureAwait(false);
        if (!got.Ok)
            return MatchDetailResult.Fail(got.Error ?? "Match failed.");
        return ParseMatchDetail(got.Body);
    }

    public static async Task<ActivityResult> GetActivityAsync(
        string? baseUrl,
        CancellationToken cancel = default)
    {
        var got = await GetPublicAsync(baseUrl, "/stats/activity", cancel).ConfigureAwait(false);
        if (!got.Ok)
            return ActivityResult.Fail(got.Error ?? "Activity failed.");
        return ParseActivity(got.Body);
    }

    public static LeaderboardResult ParseLeaderboard(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return LeaderboardResult.Fail("Empty stats response.");

        LeaderboardWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<LeaderboardWire>(json, s_json);
        }
        catch (JsonException ex)
        {
            return LeaderboardResult.Fail("Stats response was not JSON: " + ex.Message);
        }

        if (wire is null)
            return LeaderboardResult.Fail("Stats response was empty.");

        if (wire.Success == false)
            return LeaderboardResult.Fail(NonEmpty(wire.Error, "Leaderboard failed."));

        if (wire.Success != true && wire.Leaderboard is null)
            return LeaderboardResult.Fail("Stats response missing leaderboard.");

        return new LeaderboardResult
        {
            Success = true,
            Players = wire.Leaderboard ?? Array.Empty<StatsPlayer>(),
            Pagination = wire.Pagination ?? new StatsPagination(),
        };
    }

    public static PlayerStatsResult ParsePlayer(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return PlayerStatsResult.Fail("Empty stats response.");

        PlayerWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<PlayerWire>(json, s_json);
        }
        catch (JsonException ex)
        {
            return PlayerStatsResult.Fail("Stats response was not JSON: " + ex.Message);
        }

        if (wire is null)
            return PlayerStatsResult.Fail("Stats response was empty.");

        if (wire.Success == false)
            return PlayerStatsResult.Fail(NonEmpty(wire.Error, "Player stats failed."));

        if (wire.Player is null)
            return PlayerStatsResult.Fail(NonEmpty(wire.Error, "Player not found."));

        return new PlayerStatsResult
        {
            Success = true,
            Player = wire.Player,
            RecentMatches = wire.RecentMatches ?? Array.Empty<StatsMatch>(),
            Maps = wire.Maps ?? Array.Empty<MapSplit>(),
            Weapons = wire.Weapons ?? Array.Empty<WeaponSplit>(),
        };
    }

    public static MatchesResult ParseMatches(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return MatchesResult.Fail("Empty stats response.");

        MatchesWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<MatchesWire>(json, s_json);
        }
        catch (JsonException ex)
        {
            return MatchesResult.Fail("Stats response was not JSON: " + ex.Message);
        }

        if (wire is null)
            return MatchesResult.Fail("Stats response was empty.");

        if (wire.Success == false)
            return MatchesResult.Fail(NonEmpty(wire.Error, "Matches failed."));

        if (wire.Success != true && wire.Matches is null)
            return MatchesResult.Fail("Stats response missing matches.");

        return new MatchesResult
        {
            Success = true,
            Matches = wire.Matches ?? Array.Empty<StatsMatch>(),
            Pagination = wire.Pagination ?? new StatsPagination(),
        };
    }

    public static MatchSessionsResult ParseMatchSessions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return MatchSessionsResult.Fail("Empty stats response.");

        MatchSessionsWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<MatchSessionsWire>(json, s_json);
        }
        catch (JsonException ex)
        {
            return MatchSessionsResult.Fail("Stats response was not JSON: " + ex.Message);
        }

        if (wire is null)
            return MatchSessionsResult.Fail("Stats response was empty.");
        if (wire.Success == false)
            return MatchSessionsResult.Fail(NonEmpty(wire.Error, "Matches failed."));
        if (wire.Success != true && wire.Matches is null)
            return MatchSessionsResult.Fail("Stats response missing matches.");

        return new MatchSessionsResult
        {
            Success = true,
            Matches = wire.Matches ?? Array.Empty<StatsMatchSession>(),
            Pagination = wire.Pagination ?? new StatsPagination(),
        };
    }

    public static MatchDetailResult ParseMatchDetail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return MatchDetailResult.Fail("Empty stats response.");

        MatchDetailWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<MatchDetailWire>(json, s_json);
        }
        catch (JsonException ex)
        {
            return MatchDetailResult.Fail("Stats response was not JSON: " + ex.Message);
        }

        if (wire is null)
            return MatchDetailResult.Fail("Stats response was empty.");
        if (wire.Success == false || wire.Match is null)
            return MatchDetailResult.Fail(NonEmpty(wire.Error, "Match not found."));

        return new MatchDetailResult { Success = true, Match = wire.Match };
    }

    /// <summary>Uuid text only -- the id is pasted into a URL path.</summary>
    public static string SanitizeMatchId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var s = raw.Trim();
        if (s.Length != 36)
            return string.Empty;
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F')
                || c == '-';
            if (!ok)
                return string.Empty;
        }
        return s;
    }

    public static ActivityResult ParseActivity(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ActivityResult.Fail("Empty stats response.");

        ActivityWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<ActivityWire>(json, s_json);
        }
        catch (JsonException ex)
        {
            return ActivityResult.Fail("Stats response was not JSON: " + ex.Message);
        }

        if (wire is null)
            return ActivityResult.Fail("Stats response was empty.");

        if (wire.Success == false)
            return ActivityResult.Fail(NonEmpty(wire.Error, "Activity failed."));

        return new ActivityResult
        {
            Success = true,
            Servers = wire.Servers,
            Players = wire.Players,
            Capacity = wire.Capacity,
            Listings = wire.Listings ?? Array.Empty<ActivityListing>(),
        };
    }

    public static string NormalizeSort(string? sort)
    {
        var s = string.IsNullOrWhiteSpace(sort) ? "score" : sort.Trim();
        return s_sorts.Contains(s) ? s : "score";
    }

    public static string SanitizeQ(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return string.Empty;
        var chars = new char[Math.Min(q.Length, 64)];
        var n = 0;
        foreach (var c in q.Trim())
        {
            if (n >= chars.Length)
                break;
            if (c is '%' or '_' or '\\')
                continue;
            if (c < 0x20 || c > 0x7E)
                continue;
            chars[n++] = c;
        }
        return n == 0 ? string.Empty : new string(chars, 0, n).Trim();
    }

    private sealed class Fetched
    {
        public bool Ok { get; init; }
        public string Body { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    private sealed class LeaderboardWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("leaderboard")]
        public StatsPlayer[]? Leaderboard { get; init; }

        [JsonPropertyName("pagination")]
        public StatsPagination? Pagination { get; init; }
    }

    private sealed class SeasonsWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("seasons")]
        public StatsSeason[]? Seasons { get; init; }

        [JsonPropertyName("current")]
        public StatsSeason? Current { get; init; }

        [JsonPropertyName("all")]
        public StatsSeason? All { get; init; }
    }

    private sealed class PlayerWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("player")]
        public StatsPlayer? Player { get; init; }

        [JsonPropertyName("recentMatches")]
        public StatsMatch[]? RecentMatches { get; init; }

        [JsonPropertyName("maps")]
        public MapSplit[]? Maps { get; init; }

        [JsonPropertyName("weapons")]
        public WeaponSplit[]? Weapons { get; init; }
    }

    private sealed class MatchesWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("matches")]
        public StatsMatch[]? Matches { get; init; }

        [JsonPropertyName("pagination")]
        public StatsPagination? Pagination { get; init; }
    }

    private sealed class MatchSessionsWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("matches")]
        public StatsMatchSession[]? Matches { get; init; }

        [JsonPropertyName("pagination")]
        public StatsPagination? Pagination { get; init; }
    }

    private sealed class MatchDetailWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("match")]
        public StatsMatchSession? Match { get; init; }
    }

    private sealed class ActivityWire
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("servers")]
        public int Servers { get; init; }

        [JsonPropertyName("players")]
        public int Players { get; init; }

        [JsonPropertyName("capacity")]
        public int Capacity { get; init; }

        [JsonPropertyName("listings")]
        public ActivityListing[]? Listings { get; init; }
    }

    private static async Task<Fetched> GetPublicAsync(
        string? baseUrl,
        string pathAndQuery,
        CancellationToken cancel)
    {
        var root = MasterServerClient.NormalizeBaseUrl(baseUrl);
        Uri uri;
        try
        {
            uri = new Uri(root + pathAndQuery, UriKind.Absolute);
        }
        catch (Exception ex)
        {
            return new Fetched { Error = "Bad master-server URL: " + ex.Message };
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !IsLoopbackHttp(uri))
        {
            return new Fetched { Error = "Master server must be https (http is loopback-only)." };
        }

        try
        {
            using var resp = await s_http.GetAsync(uri, cancel).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new Fetched
                {
                    Error = $"Stats HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                };
            }

            return new Fetched { Ok = true, Body = text };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Fetched { Error = "Stats request timed out." };
        }
        catch (Exception ex)
        {
            return new Fetched { Error = "Could not reach stats: " + ex.Message };
        }
    }

    private static bool IsLoopbackHttp(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
