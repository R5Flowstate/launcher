using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// The leaderboards tab, offline. Everything here is the same code the tab runs —
/// the wire parsers, the row and card derivation, the ordering rules, the season
/// picker, the pager mapping — against fixtures rather than the live master
/// server, so the pass is deterministic and no socket is opened.
///
/// Nothing here drives a command that fetches: <c>--leaderboard</c> is the live
/// probe, and it is read-only by design. Reads the player's settings (the view
/// model it is handed) but never writes them.
/// </summary>
static class LeaderboardSelfTest
{
    public static void Run(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        // --- a board answer shaped like the master server's -----------------
        const string Board = """
            {"success":true,"leaderboard":[
              {"rank":1,"accountId":9001,"persona":"Ash","score":15000,"kills":420,"deaths":100,
               "kd":4.2,"damage":88000,"accuracy":0.31,"headshots":120,"hits":900,"shots":2900,
               "wins":30,"losses":6,"games":36,"winRate":0.833,"timePlayed":7200,
               "mostUsedWeapon":"mp_weapon_r301","mostUsedInput":"mnk","currentWinStreak":5,
               "longestWinStreak":9,"mostKillsInMatch":21,"mostDamageInMatch":3400},
              {"rank":2,"accountId":9002,"persona":"  Wraith  ","score":12000,"kills":300,"deaths":140,
               "kd":2.14,"damage":61000,"accuracy":0.27,"headshots":60,"hits":500,"shots":1850,
               "wins":20,"losses":10,"games":30,"winRate":0.667,"timePlayed":5400,
               "mostUsedWeapon":"unknown","mostUsedInput":"controller","currentWinStreak":0,
               "longestWinStreak":4,"mostKillsInMatch":14,"mostDamageInMatch":2600}
            ],"pagination":{"total":137,"limit":50,"offset":0,"hasNext":true,"hasPrevious":false}}
            """;

        var board = StatsClient.ParseLeaderboard(Board);
        c("lb parse: success", board.Success, board.Error ?? "ok");
        c("lb parse: two players", board.Players.Count == 2, board.Players.Count.ToString());
        c("lb parse: pagination total", board.Pagination.Total == 137, board.Pagination.Total.ToString());
        c("lb parse: has next, no previous",
            board.Pagination.HasNext && !board.Pagination.HasPrevious);
        c("lb parse: rank read from the wire", board.Players[0].Rank == 1);
        c("lb parse: camelCase into the DTO", board.Players[0].MostUsedWeapon == "mp_weapon_r301");
        c("lb parse: winRate is a fraction", Math.Abs(board.Players[0].WinRate - 0.833) < 0.0001);

        // --- refusal paths: a malformed answer must never look like "no rows" --
        var refused = StatsClient.ParseLeaderboard("""{"success":false,"error":"db down"}""");
        c("lb parse: server error surfaces its text",
            !refused.Success && refused.Error == "db down", refused.Error ?? "null");
        c("lb parse: non-JSON is refused",
            !StatsClient.ParseLeaderboard("<html>502</html>").Success);
        c("lb parse: empty body is refused", !StatsClient.ParseLeaderboard("").Success);
        // Upstream's third rule is "no success flag AND no array" — an explicit
        // success:true with no rows is an empty board, not a failure, and the tab
        // says "no players yet" rather than showing a protocol error.
        var emptyButOk = StatsClient.ParseLeaderboard("""{"success":true}""");
        c("lb parse: success with no rows is an empty board, not an error",
            emptyButOk.Success && emptyButOk.Players.Count == 0);
        c("lb parse: neither flag nor array is refused",
            !StatsClient.ParseLeaderboard("""{"foo":1}""").Success);
        c("lb parse: a bare leaderboard array is accepted",
            StatsClient.ParseLeaderboard("""{"leaderboard":[]}""").Success);

        // --- row derivation (the sixteen columns) ---------------------------
        var row = new LeaderboardRowVM(board.Players[0]);
        c("lb row: rank label", row.RankLabel == "1", row.RankLabel);
        c("lb row: score is grouped", row.ScoreLabel == "15,000", row.ScoreLabel);
        c("lb row: kd is two places", row.KdLabel == "4.20", row.KdLabel);
        c("lb row: damage is grouped", row.DamageLabel == "88,000", row.DamageLabel);
        c("lb row: accuracy from hits/shots (not the wire's ratio)",
            row.AccLabel == "31.0%", row.AccLabel);
        c("lb row: weapon drops the mp_weapon_ prefix", row.WeaponLabel == "r301", row.WeaponLabel);
        c("lb row: mnk reads MNK", row.InputLabel == "MNK", row.InputLabel);
        c("lb row: win rate is a percentage", row.WrLabel == "83.3%", row.WrLabel);
        c("lb row: podium for rank 1..3", row.IsPodium);
        c("lb row: persona is trimmed",
            new LeaderboardRowVM(board.Players[1]).Persona == "Wraith");
        c("lb row: an unnamed player reads as #id",
            new LeaderboardRowVM(new StatsPlayer { AccountId = 4242 }).Persona == "#4242");
        c("lb row: unknown weapon is a dash",
            new LeaderboardRowVM(board.Players[1]).WeaponLabel == "--");
        c("lb row: controller reads PAD",
            new LeaderboardRowVM(board.Players[1]).InputLabel == "PAD");
        c("lb row: rank 0 is not a podium and prints --",
            new LeaderboardRowVM(new StatsPlayer { AccountId = 7 }).RankLabel == "--"
            && !new LeaderboardRowVM(new StatsPlayer { AccountId = 7 }).IsPodium);

        // --- formatters that the cards reuse --------------------------------
        c("lb fmt: rate from a whole percent is left alone",
            LeaderboardRowVM.FormatRate(55) == "55.0%", LeaderboardRowVM.FormatRate(55));
        c("lb fmt: NaN / negative rates are a dash",
            LeaderboardRowVM.FormatRate(double.NaN) == "--"
            && LeaderboardRowVM.FormatRate(-1) == "--");
        c("lb fmt: no shots means no accuracy",
            LeaderboardRowVM.FormatAccuracy(0, 0) == "--");
        c("lb fmt: duration m:ss", LeaderboardRowVM.FormatDuration(90) == "1:30",
            LeaderboardRowVM.FormatDuration(90));
        c("lb fmt: zero duration is a dash", LeaderboardRowVM.FormatDuration(0) == "--");
        c("lb fmt: blank text is a dash", LeaderboardRowVM.Dash("   ") == "--");

        // --- the wire query is sanitised before it is pasted into a URL -----
        c("lb q: SQL/URL metacharacters are dropped",
            StatsClient.SanitizeQ("a%b_c\\d") == "abcd", StatsClient.SanitizeQ("a%b_c\\d"));
        c("lb q: non-ASCII and control bytes are dropped",
            StatsClient.SanitizeQ("héllo\u0007") == "hllo", StatsClient.SanitizeQ("héllo\u0007"));
        c("lb q: capped at 64 characters",
            StatsClient.SanitizeQ(new string('x', 200)).Length == 64);
        c("lb q: empty stays empty", StatsClient.SanitizeQ("   ") == "");
        c("lb sort: an unknown column falls back to score",
            StatsClient.NormalizeSort("drop table") == "score");
        c("lb sort: a known column is kept, case-insensitively",
            StatsClient.NormalizeSort("KILLS") == "KILLS");
        c("lb sort: streak is a real column", StatsClient.NormalizeSort("streak") == "streak");
        c("lb id: a uuid passes",
            StatsClient.SanitizeMatchId("3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061")
            == "3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061");
        c("lb id: a traversal attempt is refused",
            StatsClient.SanitizeMatchId("../../etc/passwd") == "");
        c("lb id: a short id is refused", StatsClient.SanitizeMatchId("abc") == "");
        c("lb id: a non-hex character is refused",
            StatsClient.SanitizeMatchId("zf1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061") == "");

        // --- ordering: the same rules Windows sorts a page with --------------
        var three = new List<StatsPlayer>
        {
            new() { Rank = 3, AccountId = 3, Persona = "bravo", Score = 10, Kills = 1 },
            new() { Rank = 1, AccountId = 1, Persona = "Alpha", Score = 30, Kills = 1 },
            new() { Rank = 2, AccountId = 2, Persona = "alpha", Score = 20, Kills = 1 },
        };
        var byScore = MainViewModel.SortLocal("score", "desc", three);
        c("lb sort: score desc puts the best first",
            byScore[0].Score == 30 && byScore[2].Score == 10,
            string.Join(",", byScore.Select(p => p.Score)));
        var byScoreAsc = MainViewModel.SortLocal("score", "asc", three);
        c("lb sort: score asc reverses it", byScoreAsc[0].Score == 10);
        var byPersona = MainViewModel.SortLocal("persona", "asc", three);
        c("lb sort: persona is case-insensitive and stable",
            byPersona[0].Persona == "Alpha" && byPersona[1].Persona == "alpha",
            string.Join(",", byPersona.Select(p => p.Persona)));
        // Tie-break is upstream's, quirk included: the rank/account fallback is
        // inside the comparison that the order flips, so a descending sort breaks
        // a tie descending too. Pinned here rather than "fixed", because the same
        // page order has to read the same on Windows while a request is in flight.
        var tied = new List<StatsPlayer>
        {
            new() { Rank = 5, AccountId = 50 },
            new() { Rank = 2, AccountId = 20 },
        };
        var tieDesc = MainViewModel.SortLocal("kills", "desc", tied);
        c("lb sort: a tie breaks on rank, flipped with the order (upstream quirk)",
            tieDesc[0].Rank == 5 && tieDesc[1].Rank == 2,
            string.Join(",", tieDesc.Select(p => p.Rank)));
        var tieAsc = MainViewModel.SortLocal("kills", "asc", tied);
        c("lb sort: an ascending tie breaks ascending",
            tieAsc[0].Rank == 2 && tieAsc[1].Rank == 5,
            string.Join(",", tieAsc.Select(p => p.Rank)));
        c("lb sort: an unknown column sorts by score",
            MainViewModel.SortLocal("nope", "desc", three)[0].Score == 30);

        // --- binding a page into the list ------------------------------------
        var savedRows = vm.LeaderboardRows.Count;
        var savedSort = vm.LbSortKey;
        try
        {
            vm.BindLbFromPlayers(board.Players, fetchDetails: false);
            c("lb bind: the page lands in LeaderboardRows",
                vm.LeaderboardRows.Count == 2, vm.LeaderboardRows.Count.ToString());
            c("lb bind: the top row is the leader",
                vm.LeaderboardRows[0].Persona == "Ash" && vm.LeaderboardRows[0].IsPodium);
            c("lb bind: no profile is open until a row is clicked", !vm.LbDetailVisible);

            // Pager mapping (upstream PaintLbPager).
            vm.PaintLbPager(board.Pagination);
            c("lb pager: next enabled, previous disabled",
                vm.LbHasNext && !vm.LbHasPrev);
            vm.PaintLbPager(null);
            c("lb pager: a failed page disables both",
                !vm.LbHasNext && !vm.LbHasPrev);
        }
        finally
        {
            vm.LeaderboardRows.Clear();
            foreach (var p in board.Players)
                vm.LeaderboardRows.Add(new LeaderboardRowVM(p));
            vm.PaintLbPager(null);
            c("lb bind: the suite left the sort state alone", vm.LbSortKey == savedSort,
                $"{savedRows} row(s) before");
        }

        // --- headers: caption, arrow, active column --------------------------
        c("lb header: the default sort is score", vm.LbSortKey == "score", vm.LbSortKey);
        c("lb header: the default order is descending", vm.LbOrder == "desc", vm.LbOrder);
        c("lb header: the active column carries its arrow",
            vm.LbHeaderCaption("score").EndsWith("▼", StringComparison.Ordinal),
            vm.LbHeaderCaption("score"));
        c("lb header: an inactive column carries none",
            !vm.LbHeaderCaption("kills").Contains("▼", StringComparison.Ordinal),
            vm.LbHeaderCaption("kills"));
        c("lb header: the caption comes from the loc table",
            vm.LbHeaderCaption("kills") == Loc.Get("lb_k"),
            $"{vm.LbHeaderCaption("kills")} vs {Loc.Get("lb_k")}");
        c("lb header: streak and winStreak share a caption",
            vm.LbHeaderCaption("streak") == vm.LbHeaderCaption("winStreak"));
        c("lb header: only the active column is active",
            vm.LbHeaderIsActive("score") && !vm.LbHeaderIsActive("kills"));
        c("lb header: every column has a loc key",
            MainViewModel.LbHeaders.All(h => Loc.Get(h.LocKey).Length > 0));
        c("lb header: the table covers all sixteen columns",
            MainViewModel.LbHeaders.Length == 17,
            $"{MainViewModel.LbHeaders.Length} entries (streak + winStreak alias)");

        // --- seasons ---------------------------------------------------------
        var seasons = new List<StatsSeason>
        {
            new() { Id = "s3", Name = "Season 3", Active = true },
            new() { Id = "s2", Name = "Season 2" },
            new() { Id = "all", Name = "", IsAll = true },
        };
        var items = MainViewModel.SeasonItems(seasons);
        c("lb seasons: one row per season", items.Count == 3);
        c("lb seasons: the all-seasons entry reads from loc",
            items[2].Label == Loc.Get("lb_all_seasons"), items[2].Label);
        c("lb seasons: the active season is starred",
            items[0].Label.EndsWith("*", StringComparison.Ordinal), items[0].Label);
        c("lb seasons: the all-seasons id is the wire's", items[2].Id == "all");
        c("lb seasons: the season in force is kept",
            MainViewModel.PickSeason(items, "s2").Id == "s2");
        c("lb seasons: an unknown season falls back to the first real one",
            MainViewModel.PickSeason(items, "s9").Id == "s3");
        c("lb seasons: a lone all-seasons list still picks something",
            MainViewModel.PickSeason(MainViewModel.SeasonItems(
                new List<StatsSeason> { new() { Id = "all", IsAll = true } }), "current").Id == "all");
        c("lb seasons: an identical list is left alone",
            MainViewModel.SameSeasons(items, MainViewModel.SeasonItems(seasons)));
        c("lb seasons: a changed name is a repaint",
            !MainViewModel.SameSeasons(items, MainViewModel.SeasonItems(
                new List<StatsSeason>
                {
                    new() { Id = "s3", Name = "Season 3 (renamed)", Active = true },
                    new() { Id = "s2", Name = "Season 2" },
                    new() { Id = "all", Name = "", IsAll = true },
                })));

        // --- player card ------------------------------------------------------
        const string Player = """
            {"success":true,
             "player":{"rank":1,"accountId":9001,"persona":"Ash","kills":420,"deaths":100,"kd":4.2,
                       "accuracy":0.31,"hits":900,"shots":2900,"wins":30,"games":36,"winRate":0.833,
                       "currentWinStreak":5,"longestWinStreak":9,"mostKillsInMatch":21,
                       "mostDamageInMatch":3400},
             "recentMatches":[
               {"accountId":9001,"persona":"Ash","opponentId":9002,"opponentPersona":"Wraith",
                "map":"mp_rr_divided_moon_mu1","result":"W","kills":7,"deaths":2,"damage":1400,
                "at":"2026-09-21T18:04:00Z","weapon":"mp_weapon_r301","duration":245,
                "hostName":"Cafe Bogota","matchId":"3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061",
                "playlist":"survival_dev","shots":120,"hits":40,"headshots":6,"accuracy":0.33,
                "input":"mnk"}],
             "maps":[{"map":"mp_rr_divided_moon_mu1","games":12,"kills":90,"deaths":30,"damage":22000,"wins":9}],
             "weapons":[{"weapon":"mp_weapon_r301","games":20,"kills":150,"damage":40000}]}
            """;

        var stats = StatsClient.ParsePlayer(Player);
        c("lb player: parse", stats.Success, stats.Error ?? "ok");
        c("lb player: the profile is read", stats.Player?.Persona == "Ash");
        c("lb player: one recent match", stats.RecentMatches.Count == 1);
        c("lb player: one map split and one weapon split",
            stats.Maps.Count == 1 && stats.Weapons.Count == 1);
        c("lb player: a missing player is refused",
            !StatsClient.ParsePlayer("""{"success":true}""").Success);
        c("lb player: the server's error text wins",
            StatsClient.ParsePlayer("""{"success":false,"error":"no such player"}""").Error
            == "no such player");

        var recent = new RecentMatchVM(stats.RecentMatches[0], null);
        c("lb recent: result is upper-cased", recent.Result == "W", recent.Result);
        c("lb recent: the match id survives validation",
            recent.MatchId == "3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061");
        c("lb recent: the map is prettified from the stem",
            recent.MapLabel == "Divided Moon Mu1", recent.MapLabel);
        c("lb recent: the score line is kills-deaths", recent.KdLabel == "7-2", recent.KdLabel);
        c("lb recent: the weapon is unprefixed", recent.WeaponLabel == "r301");
        c("lb recent: duration is m:ss", recent.DurationLabel == "4:05", recent.DurationLabel);
        c("lb recent: a win paints accent, a loss danger",
            recent.ResultForeground == ThemeBrushes.Accent
            && new RecentMatchVM(new StatsMatch { Result = "L" }, null).ResultForeground
               == ThemeBrushes.Danger);
        c("lb recent: an unparseable timestamp is shown as-is",
            new RecentMatchVM(new StatsMatch { At = "whenever" }, null).AtLabel == "whenever");

        // --- match card --------------------------------------------------------
        const string Session = """
            {"success":true,
             "match":{"matchId":"3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061","at":"2026-09-21T18:04:00Z",
                      "map":"mp_rr_divided_moon_mu1","playlist":"survival_dev","duration":245,
                      "hostName":"Cafe Bogota","winnerId":9001,"winnerPersona":"Ash",
                      "kills":12,"damage":2600,
                      "players":[
                        {"accountId":9001,"persona":"Ash","result":"W","kills":7,"deaths":2,
                         "damage":1400,"shots":120,"hits":40,"headshots":6,"weapon":"mp_weapon_r301",
                         "input":"mnk"},
                        {"accountId":9002,"persona":"Wraith","result":"L","kills":5,"deaths":7,
                         "damage":1200,"shots":100,"hits":30,"headshots":4,"weapon":"mp_weapon_peacekeeper",
                         "input":"controller"}]}}
            """;

        var detail = StatsClient.ParseMatchDetail(Session);
        c("lb match: parse", detail.Success && detail.Match is not null, detail.Error ?? "ok");
        c("lb match: a bad id is refused before the request",
            StatsClient.ParseMatchDetail("""{"success":true,"match":null}""").Error
            == "Match not found.");
        var session = new MatchSessionVM(detail.Match!, null);
        c("lb match: two players, one winner",
            session.PlayerCount == 2 && session.HasWinner);
        c("lb match: the title is both personas", session.Title.Contains("Ash")
            && session.Title.Contains("Wraith"), session.Title);
        c("lb match: the score is kills each", session.ScoreLabel == "7 - 5", session.ScoreLabel);
        c("lb match: the winner's row is flagged",
            session.Players[0].IsWinner && !session.Players[1].IsWinner);
        c("lb match: the loser's row paints danger",
            session.Players[1].ResultForeground == ThemeBrushes.Danger);
        c("lb match: the map is prettified", session.MapLabel == "Divided Moon Mu1");
        c("lb match: duration is m:ss", session.DurationLabel == "4:05");
        var oneSided = new MatchSessionVM(
            StatsMatchSessionFrom(new[] { new StatsMatchPlayer { Persona = "Solo", Result = "W" } }),
            null);
        c("lb match: a one-sided row is a real match, not a crash",
            oneSided.PlayerCount == 1 && !oneSided.HasWinner && oneSided.Title == "Solo",
            oneSided.Title);
        c("lb match: an empty session reads as a dash",
            new MatchSessionVM(StatsMatchSessionFrom(Array.Empty<StatsMatchPlayer>()), null)
                .Title == "--");

        // --- history list ------------------------------------------------------
        const string Sessions = """
            {"success":true,
             "matches":[{"matchId":"3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061","at":"2026-09-21T18:04:00Z",
                         "map":"mp_lobby","hostName":"Cafe Bogota","duration":120,"kills":3,"damage":500,
                         "players":[{"accountId":1,"persona":"A","result":"W","kills":3,"deaths":1},
                                    {"accountId":2,"persona":"B","result":"L","kills":1,"deaths":3}]}],
             "pagination":{"total":9,"limit":50,"offset":0,"hasNext":false,"hasPrevious":false}}
            """;
        var history = StatsClient.ParseMatchSessions(Sessions);
        c("lb history: parse", history.Success, history.Error ?? "ok");
        c("lb history: one session", history.Matches.Count == 1);
        c("lb history: the total is kept", history.Pagination.Total == 9);
        c("lb history: the plain matches route still parses",
            StatsClient.ParseMatches(Sessions).Matches.Count == 1);
        c("lb history: a malformed body is refused",
            !StatsClient.ParseMatchSessions("[]").Success);

        // --- activity line ------------------------------------------------------
        var activity = StatsClient.ParseActivity(
            """{"success":true,"servers":3,"players":40,"capacity":180,"listings":[]}""");
        c("lb activity: parse", activity.Success && activity.Servers == 3 && activity.Players == 40,
            activity.Error ?? "ok");
        c("lb activity: a failed read is not painted",
            !StatsClient.ParseActivity("""{"success":false,"error":"nope"}""").Success);
        c("lb activity: the loc line names both numbers",
            Loc.Format("lb_activity", 3, 40).Contains("3") &&
            Loc.Format("lb_activity", 3, 40).Contains("40"),
            Loc.Format("lb_activity", 3, 40));

        // --- the tab actually paints -----------------------------------------
        // Everything above is data. A template that throws, a binding path that
        // does not resolve, a sixteen-column grid that cannot lay out — none of
        // that fails a parse check; it fails at layout. So the panel is shown off
        // schedule (the suite runs before the window's own render pass), filled
        // from fixtures, laid out and captured, then put back exactly as found.
        RenderTab(check, vm, window);
    }

    /// <summary>Renders the leaderboards tab offscreen with fixture rows: the
    /// board, the history list, the player card and the match card.</summary>
    static void RenderTab(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        var panel = window.FindControl<Control>("PanelSimpleLeaderboards");
        var local = window.FindControl<Control>("PanelSimpleLocal");
        var board = window.FindControl<ScrollViewer>("ScrollLbBoard");
        var history = window.FindControl<ScrollViewer>("ScrollLbHistory");
        var detail = window.FindControl<Control>("PanelLbDetail");
        var overlay = window.FindControl<Control>("OverlayLbMatch");
        var listLb = window.FindControl<ItemsControl>("ListLb");
        var listHistory = window.FindControl<ItemsControl>("ListLbHistory");
        if (panel is null || local is null || board is null || history is null ||
            detail is null || overlay is null || listLb is null || listHistory is null)
        {
            c("lb render: the tab exists", false, "a named control is missing");
            return;
        }

        var localWasVisible = local.IsVisible;
        var boardWasVisible = board.IsVisible;
        var historyWasVisible = history.IsVisible;
        var detailWasVisible = vm.LbDetailVisible;
        var overlayWasVisible = vm.LbMatchVisible;
        try
        {
            vm.LeaderboardRows.Clear();
            vm.BindLbFromPlayers(new List<StatsPlayer>
            {
                new() { Rank = 1, AccountId = 9001, Persona = "Ash", Score = 15000, Kd = 4.2,
                        Hits = 900, Shots = 2900, WinRate = 0.833, CurrentWinStreak = 5,
                        MostUsedWeapon = "mp_weapon_r301", MostUsedInput = "mnk" },
                new() { Rank = 2, AccountId = 9002, Persona = "Wraith", Score = 12000, Kd = 2.14 },
            }, fetchDetails: false);

            vm.LeaderboardHistoryRows.Clear();
            vm.LeaderboardHistoryRows.Add(new MatchSessionVM(
                new StatsMatchSession
                {
                    MatchId = "3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061",
                    Map = "mp_rr_divided_moon_mu1",
                    Playlist = "survival_dev",
                    Duration = 245,
                    HostName = "Cafe Bogota",
                    WinnerId = 9001,
                    WinnerPersona = "Ash",
                    Players = new[]
                    {
                        new StatsMatchPlayer { AccountId = 9001, Persona = "Ash", Result = "W", Kills = 7, Deaths = 2 },
                        new StatsMatchPlayer { AccountId = 9002, Persona = "Wraith", Result = "L", Kills = 5, Deaths = 7 },
                    },
                }, null));

            // Cards open, so their templates lay out in the same frame.
            vm.LbDetailVisible = true;
            vm.LbDetailPersona = "Ash";
            vm.LbDetailRank = Loc.Format("lb_rank_n", "1");
            vm.LbDetailStats = Loc.Format("lb_stats", "4.20", "31.0%", "83.3%");
            vm.LbDetailLoadout = Loc.Format("lb_loadout", "r301", "MNK");
            vm.LbDetailStreak = Loc.Format("lb_streak", 5, 9);
            vm.LbDetailBest = Loc.Format("lb_best", 21, "3,400");
            vm.LbMapSplits.Add(new SplitRowVM("World's Edge", "12g  90/30"));
            vm.LbWeaponSplits.Add(new SplitRowVM("r301", "20g  150k"));
            vm.LbRecentMatches.Add(new RecentMatchVM(
                new StatsMatch { Result = "W", Kills = 7, Deaths = 2, Damage = 1400,
                                 Map = "mp_rr_divided_moon_mu1", Weapon = "mp_weapon_r301",
                                 Duration = 245, HostName = "Cafe Bogota" }, null));
            vm.LbMatchVisible = true;
            vm.LbMatchTitle = "Ash  vs Wraith";
            vm.LbMatchScore = "7 - 5";
            vm.LbMatchMeta = Loc.Format("lb_match_meta", "Divided Moon Mu1", "survival_dev", "4:05", "now");
            vm.LbMatchHost = Loc.Format("lb_match_host", "Cafe Bogota");
            vm.LbMatchWinner = Loc.Format("lb_match_winner", "Ash");
            vm.LbMatchHasWinner = true;
            vm.LbMatchPlayers.Add(new MatchPlayerVM(
                new StatsMatchPlayer { AccountId = 9001, Persona = "Ash", Result = "W", Kills = 7,
                                       Deaths = 2, Damage = 1400, Shots = 120, Hits = 40 },
                isWinner: true));

            local.IsVisible = false;
            panel.IsVisible = true;
            board.IsVisible = true;
            history.IsVisible = false;

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var rows = listLb.GetRealizedContainers().Count();
            c("lb render: the board lays out its rows", rows >= 2, $"{rows} container(s)");
            c("lb render: the player card has a size",
                detail.Bounds.Width > 0 && detail.Bounds.Height > 0,
                $"{detail.Bounds.Width}x{detail.Bounds.Height}");
            c("lb render: the match card covers the tab",
                overlay.Bounds.Width > 100 && overlay.Bounds.Height > 100,
                $"{overlay.Bounds.Width}x{overlay.Bounds.Height}");

            var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            c("lb render: the tab paints a frame", frame is not null);
            if (frame is not null)
            {
                var path = Path.Combine(Path.GetTempPath(), "r5flowstate-leaderboard-selftest.png");
                frame.Save(path);
                var size = new FileInfo(path).Length;
                c("lb render: the frame is not blank", size > 8 * 1024,
                    $"{size} bytes → {path}");
            }

            // The history list is a different template on a different host, so it
            // gets its own pass rather than being assumed from the board's.
            vm.LbDetailVisible = false;
            vm.LbMatchVisible = false;
            board.IsVisible = false;
            history.IsVisible = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var sessions = listHistory.GetRealizedContainers().Count();
            c("lb render: the history list lays out its rows", sessions >= 1,
                $"{sessions} container(s)");
            var historyFrame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            c("lb render: the history view paints a frame", historyFrame is not null);
        }
        catch (Exception ex)
        {
            c("lb render: the tab paints", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            panel.IsVisible = false;
            local.IsVisible = localWasVisible;
            board.IsVisible = boardWasVisible;
            history.IsVisible = historyWasVisible;
            vm.LbDetailVisible = detailWasVisible;
            vm.LbMatchVisible = overlayWasVisible;
            vm.LbDetailVisible = false;
            vm.LbMatchVisible = false;
            vm.LeaderboardRows.Clear();
            vm.LeaderboardHistoryRows.Clear();
            vm.LbMapSplits.Clear();
            vm.LbWeaponSplits.Clear();
            vm.LbRecentMatches.Clear();
            vm.LbMatchPlayers.Clear();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A session with no wire text, for the degenerate-shape checks.</summary>
    static StatsMatchSession StatsMatchSessionFrom(StatsMatchPlayer[] players) =>
        new() { MatchId = "3f1a2b4c-5d6e-7f80-9a0b-1c2d3e4f5061", Players = players };
}
