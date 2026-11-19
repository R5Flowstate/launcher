using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the public stats surface behind the Leaderboards tab: the
/// board, the seasons, one player and their splits, the match history and the
/// activity line. Nothing is written anywhere — no setting changes, no profile
/// opened, no match joined — so it answers "what would the tab show right now?"
/// without opening the launcher.
///
///     R5Flowstate --leaderboard
///     R5Flowstate --leaderboard --sort kills --order asc --q ash
///     R5Flowstate --leaderboard --season all --limit 5
///
/// Every route it touches is a GET on the public stats API; the same calls the
/// tab makes, with the same sanitising of the sort key, the order and the query.
/// </summary>
static class LeaderboardCheck
{
    public static async Task<int> RunAsync(string? sort, string? order, string? season, string? q, string? limit)
    {
        Loc.Initialize(null);

        var url = MainViewModel.MasterServerUrl;
        sort = StatsClient.NormalizeSort(sort);
        order = string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var query = StatsClient.SanitizeQ(q);
        var take = StatsClient.DefaultLimit;
        if (int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            take = Math.Clamp(parsed, 1, 100);

        Console.WriteLine($"Master:   {url}");
        Console.WriteLine($"Board:    sort={sort} order={order}" +
                          (string.IsNullOrWhiteSpace(season) ? "" : $" season={season.Trim()}") +
                          (query.Length > 0 ? $" q={query}" : "") +
                          $" limit={take}");
        Console.WriteLine();

        var failures = 0;

        // --- seasons: the combo the tab fills before its first board request ---
        var seasons = await StatsClient.GetSeasonsAsync(url, CancellationToken.None).ConfigureAwait(false);
        if (seasons.Success)
        {
            Console.WriteLine($"OK    {seasons.Seasons.Count} season(s)");
            var items = MainViewModel.SeasonItems(seasons.Seasons);
            foreach (var s in items.Take(12))
                Console.WriteLine($"  {s.Id,-14} {Trim(s.Label, 40),-40}{Flag(s.Id == "all", "all-seasons")}");
            if (items.Count > 12)
                Console.WriteLine($"  … and {items.Count - 12} more");
            var pick = MainViewModel.PickSeason(items, string.IsNullOrWhiteSpace(season) ? "current" : season.Trim());
            Console.WriteLine($"      the tab would select: {pick.Id} ({pick.Label})");
        }
        else
        {
            failures++;
            Console.WriteLine($"FAIL  seasons: {seasons.Error}");
        }

        Console.WriteLine();

        // --- the board itself -------------------------------------------------
        var board = await StatsClient.GetLeaderboardAsync(
                url, sort, order, take, 0, CancellationToken.None, season, query)
            .ConfigureAwait(false);
        if (!board.Success)
        {
            failures++;
            Console.WriteLine($"FAIL  board: {board.Error}");
        }
        else
        {
            Console.WriteLine($"OK    {board.Players.Count} row(s), " +
                              $"total {board.Pagination.Total}, " +
                              $"next={Flag(board.Pagination.HasNext, "yes")} " +
                              $"prev={Flag(board.Pagination.HasPrevious, "yes")}");
            Console.WriteLine();
            Console.WriteLine("  rank  player                 score     k/d    acc    wr     weapon            input  wins/games");
            foreach (var p in board.Players.Take(20))
            {
                var row = new LeaderboardRowVM(p);
                Console.WriteLine($"  {row.RankLabel,4}  {Trim(row.Persona, 20),-20} " +
                                  $"{row.ScoreLabel,8}  {row.KdLabel,6} {row.AccLabel,6} {row.WrLabel,6} " +
                                  $"{Trim(row.WeaponLabel, 16),-16} {row.InputLabel,-6} " +
                                  $"{row.WinsLabel}/{row.GamesLabel}" +
                                  (row.IsPodium ? "  *" : ""));
            }
            if (board.Players.Count > 20)
                Console.WriteLine($"  … and {board.Players.Count - 20} more");
        }

        Console.WriteLine();

        // --- one player's card, driven off whoever the board put first -------
        var probe = board.Success ? board.Players.FirstOrDefault(p => p.AccountId > 0) : null;
        if (probe is null)
        {
            Console.WriteLine("SKIP  player card: the board was empty (nothing to open)");
        }
        else
        {
            var card = await StatsClient.GetPlayerAsync(
                    url, probe.AccountId, CancellationToken.None, season)
                .ConfigureAwait(false);
            if (!card.Success || card.Player is null)
            {
                failures++;
                Console.WriteLine($"FAIL  player card ({probe.Persona}): {card.Error}");
            }
            else
            {
                var row = new LeaderboardRowVM(card.Player);
                Console.WriteLine($"OK    player card: {row.Persona} (account {card.Player.AccountId})");
                Console.WriteLine($"      {Loc.Get("lb_rank")}: " +
                                  (card.Player.Rank > 0 ? row.RankLabel : Loc.Get("lb_unranked")) +
                                  $"   {Loc.Format("lb_stats", row.KdLabel, row.AccLabel, row.WrLabel)}");
                Console.WriteLine($"      {Loc.Format("lb_loadout", row.WeaponLabel, row.InputLabel)}");
                Console.WriteLine($"      {Loc.Format("lb_streak", card.Player.CurrentWinStreak, card.Player.LongestWinStreak)}");
                Console.WriteLine($"      {Loc.Format("lb_best", card.Player.MostKillsInMatch, card.Player.MostDamageInMatch.ToString("N0", CultureInfo.InvariantCulture))}");

                Console.WriteLine($"      {Loc.Get("lb_maps")}:");
                foreach (var m in card.Maps.Take(6))
                    Console.WriteLine($"        {Trim(MapLabels.PlayerName(m.Map, null), 26),-26} " +
                                      $"{m.Games,3}g  {m.Kills}/{m.Deaths}");
                Console.WriteLine($"      {Loc.Get("lb_weapons")}:");
                foreach (var w in card.Weapons.Take(6))
                    Console.WriteLine($"        {Trim(LeaderboardRowVM.FormatWeapon(w.Weapon), 26),-26} " +
                                      $"{w.Games,3}g  {w.Kills}k");

                Console.WriteLine($"      {Loc.Get("section_recent")}:");
                foreach (var m in card.RecentMatches.Take(8))
                {
                    var recent = new RecentMatchVM(m, null);
                    Console.WriteLine($"        {recent.Result,-2} {recent.KdLabel,7} " +
                                      $"{Trim(recent.Opponent, 16),-16} {Trim(recent.MapLabel, 20),-20} " +
                                      $"{recent.DurationLabel,5}  {Trim(recent.AtLabel, 20)}" +
                                      (recent.MatchId.Length > 0 ? "" : "  [no match id]"));
                }
            }
        }

        Console.WriteLine();

        // --- match history ----------------------------------------------------
        var history = await StatsClient.GetMatchSessionsAsync(
                url, null, map: null, take, 0, CancellationToken.None, season)
            .ConfigureAwait(false);
        if (!history.Success)
        {
            failures++;
            Console.WriteLine($"FAIL  history: {history.Error}");
        }
        else
        {
            Console.WriteLine($"OK    history: {history.Matches.Count} session(s), " +
                              $"total {history.Pagination.Total}");
            foreach (var m in history.Matches.Take(8))
            {
                var session = new MatchSessionVM(m, null);
                Console.WriteLine($"        {Trim(session.ScoreLabel, 9),-9} {Trim(session.Title, 34),-34} " +
                                  $"{Trim(session.MapLabel, 20),-20} {session.DurationLabel,5}  " +
                                  Trim(session.AtLabel, 20));
            }
        }

        Console.WriteLine();

        // --- activity line ----------------------------------------------------
        var activity = await StatsClient.GetActivityAsync(url, CancellationToken.None).ConfigureAwait(false);
        if (activity.Success)
        {
            var line = activity.Servers > 0 || activity.Players > 0
                ? Loc.Format("lb_activity", activity.Servers, activity.Players)
                : "(nothing to report — the tab shows no line at all)";
            Console.WriteLine($"OK    activity: {line}  (capacity {activity.Capacity})");
        }
        else
        {
            Console.WriteLine($"WARN  activity: {activity.Error} (the header line simply stays hidden)");
        }

        Console.WriteLine();
        Console.WriteLine("Nothing was written: no setting changed, no profile opened, no match joined.");
        return failures == 0 ? 0 : 1;
    }

    static string Flag(bool on, string label) => on ? label : "";

    static string Trim(string? s, int max)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0)
            return "--";
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
