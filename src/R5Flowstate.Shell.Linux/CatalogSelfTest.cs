using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// The playlist catalog, offline: the parser, the localization chain that turns a
/// token into a display name, the map list the install actually has, and the view
/// model wiring that hands all of it to the combos.
///
/// Most of it runs against a fixture install root built in a temp folder — a
/// playlist def, a localization file, a couple of loose maps — because a machine
/// without the game has nothing else, and the fixture is the only way to pin the
/// parser's rules one at a time. The last pass runs against the player's own
/// install when it is there (read-only): its def, its localization file and its
/// curated map list are the real thing, and they are what proves the ported rtech
/// hash matches the keys the install ships. The catalog itself is the ported
/// upstream file (Linux.Core/PlaylistCatalog.cs), so these checks are what stands
/// between it and a silent drift from Windows.
/// </summary>
static class CatalogSelfTest
{
    public static void Run(Action<string, bool, string> check, MainViewModel vm)
    {
        void c(string name, bool ok, string detail = "") => check(name, ok, detail);

        var root = Path.Combine(Path.GetTempPath(), "r5f-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteFixture(root);
            HashChecks(c, vm);
            ParseChecks(c, root);
            WiringChecks(c, vm, root);
            RealInstallChecks(c, vm);
        }
        catch (Exception ex)
        {
            c("catalog: the fixture ran", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ hash

    /// <summary>
    /// The loc keys are rtech hashes, so a wrong hash is an invisible failure:
    /// every name quietly falls back to the stem and nothing looks broken. Two
    /// kinds of check — the shape the algorithm's own doc states (case folds,
    /// backslash folds to slash, a NUL ends it, the '#' is not part of the
    /// token), and then the real thing: every name token the shipped def writes
    /// has to find the key the install's own localization file carries. That
    /// second one is the evidence that matters, and it needs an install, so it
    /// says so plainly when it cannot run rather than passing quietly.
    /// </summary>
    static void HashChecks(Action<string, bool, string> c, MainViewModel vm)
    {
        c("catalog: a backslash and a slash hash alike (the engine folds them)",
            RtechHash.HashName64("a\\b") == RtechHash.HashName64("a/b"),
            $"{RtechHash.HashName64("a\\b"):x} vs {RtechHash.HashName64("a/b"):x}");

        c("catalog: case folds, so a token's case cannot change its key",
            RtechHash.HashName64("mixed_case") == RtechHash.HashName64("MiXeD_CaSe"), "");

        c("catalog: the '#' a def writes is stripped before hashing",
            RtechHash.LocKeyFromToken("#PL_FREE_ROAM")
            == RtechHash.LocKeyFromToken("PL_FREE_ROAM"),
            RtechHash.LocKeyFromToken("#PL_FREE_ROAM"));

        c("catalog: an empty token has no key",
            RtechHash.LocKeyFromToken("") == "" && RtechHash.LocKeyFromToken("#") == "", "");

        var defPath = DefPathOf(vm.InstallPath);
        var locPath = LocPathOf(vm.InstallPath);
        if (defPath is null || locPath is null)
        {
            c("catalog: the hash is checked against the install's own loc keys",
                true,
                $"not checked: no install at {vm.InstallPath} — the shape checks above are all that ran");
            return;
        }

        var loc = PlaylistCatalogLoader.LoadLocalization(locPath);
        var tokens = NameTokens(File.ReadAllText(defPath));
        var missing = tokens
            .Where(t => !loc.ContainsKey(RtechHash.LocKeyFromToken(t)))
            .ToList();

        // Three tokens are expected to be absent, and they are worth naming:
        // EMPTY_STRING is the def's own "no name" sentinel, PL_default_name is a
        // default the code supplies, and PL_FREE_ROAM is a Flowstate mode the
        // *launcher* names (mode_title.survival_dev) because retail's loc never
        // carried it — verified on this install: neither the key nor the string
        // "Free Roam" is in its localization file. Everything else must resolve,
        // or the ported hash is wrong and half the names quietly go missing.
        string[] expectedAbsent = { "EMPTY_STRING", "PL_default_name", "PL_FREE_ROAM" };
        var unexplained = missing.Where(t => !expectedAbsent.Contains(t)).ToList();

        c("catalog: every name token in the install's own def resolves in its loc file",
            tokens.Count > 0 && unexplained.Count == 0,
            unexplained.Count == 0
                ? $"{tokens.Count - missing.Count}/{tokens.Count} resolved against {loc.Count} shipped key(s)"
                  + $" (absent by design: {string.Join(", ", missing)})"
                : $"{unexplained.Count} unexplained: " + string.Join(", ", unexplained.Take(5)));

        c("catalog: a token nobody shipped resolves to nothing",
            !loc.ContainsKey(RtechHash.LocKeyFromToken("PL_NOT_A_TOKEN_ANYWHERE_ZZZ")), "");
    }

    /// <summary>
    /// The tokens the def writes as a *name* — the value of name/map_name and the
    /// r5f_mode_* labelling vars. Not every <c>#TOKEN</c> in the file is one: the
    /// def also hands tokens to option vars (CONTROLLER_OPTION_A, calevent) that
    /// no localization file carries, and upstream leaves those alone. Naming the
    /// vars is what keeps this check about names rather than about every '#'
    /// character in a 5800-line file.
    /// </summary>
    static List<string> NameTokens(string defText) =>
        System.Text.RegularExpressions.Regex
            .Matches(defText, @"(?im)^\s*(?:name|map_name|r5f_mode_blurb|r5f_mode_title" +
                              @"|r5f_mode_map_title|r5f_mode_family_title|r5f_mode_family_blurb)\s+#([A-Za-z][A-Za-z0-9_]{2,})")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    static string? DefPathOf(string install) =>
        File.Exists(Path.Combine(install, "platform", "playlists_r5_patch.txt"))
            ? Path.Combine(install, "platform", "playlists_r5_patch.txt")
            : null;

    static string? LocPathOf(string install) =>
        File.Exists(Path.Combine(install, "platform", "localization", "localization_english.txt"))
            ? Path.Combine(install, "platform", "localization", "localization_english.txt")
            : null;

    // ----------------------------------------------------------------- parse

    static void ParseChecks(Action<string, bool, string> c, string root)
    {
        var catalog = PlaylistCatalogLoader.Load(root, "english", includeUnlistedMaps: false, UiLoc);

        c("catalog: the install's own playlist def is what gets read",
            catalog.SourcePath is not null
            && Path.GetFileName(catalog.SourcePath) == "playlists_r5_patch.txt",
            catalog.SourcePath ?? "(none)");

        c("catalog: every playlist in the def is parsed",
            catalog.Entries.Count == 3,
            string.Join(", ", catalog.Entries.Select(e => e.Id)));

        var dev = catalog.Entries.FirstOrDefault(e => e.Id == "survival_dev");
        c("catalog: a playlist's maps are its own",
            dev is not null
            && dev.Maps.Count == 2
            && dev.Maps.Contains("mp_rr_divided_moon_mu1", StringComparer.OrdinalIgnoreCase),
            dev is null ? "(no survival_dev)" : string.Join(", ", dev.Maps));

        c("catalog: r5f_mode marks a mode, and its order is kept",
            dev is { IsMode: true, ModeOrder: 10 },
            dev is null ? "(no survival_dev)" : $"mode={dev.IsMode}, order={dev.ModeOrder}");

        c("catalog: a playlist without r5f_mode is not offered as a mode",
            catalog.Entries.FirstOrDefault(e => e.Id == "survival") is { IsMode: false }
            && catalog.HostPlaylists.All(e => e.Id != "survival"),
            string.Join(", ", catalog.HostPlaylists.Select(e => e.Id)));

        c("catalog: the host list is modes first, by their own order",
            catalog.HostPlaylists.Count == 2
            && catalog.HostPlaylists[0].Id == "survival_dev"
            && catalog.HostPlaylists[1].Id == "lobby",
            string.Join(" > ", catalog.HostPlaylists.Select(e => $"{e.Id}({e.ModeOrder})")));

        // The loc chain: token -> rtech key -> the def's own localization file.
        // DisplayName is the label *with* the id appended (that is what the
        // Advanced combo shows, so a player can see what will be launched);
        // Title is the label alone, which is what a mode card carries.
        c("catalog: a name token resolves through the def's localization file",
            dev is not null && dev.Title == "Free Roam" && dev.DisplayName.StartsWith("Free Roam"),
            dev is null ? "(none)" : $"Title='{dev.Title}', DisplayName='{dev.DisplayName}'");

        c("catalog: a blurb token resolves the same way",
            dev is not null && dev.Blurb == "Explore any map. No ring, no match, no timer.",
            dev?.Blurb ?? "(none)");

        // The uiLoc fallback: strings the launcher ships, not the install.
        c("catalog: a name the def does not carry falls back to the launcher's own strings",
            catalog.Entries.FirstOrDefault(e => e.Id == "lobby")?.Title == "Lobby",
            catalog.Entries.FirstOrDefault(e => e.Id == "lobby")?.Title ?? "(none)");

        c("catalog: r5f_mode_map pins the one map the mode runs",
            catalog.Entries.FirstOrDefault(e => e.Id == "lobby")?.PinnedMap == "mp_lobby",
            catalog.Entries.FirstOrDefault(e => e.Id == "lobby")?.PinnedMap ?? "(none)");

        c("catalog: the map list is the union of every playlist's maps",
            catalog.AllMaps.Contains("mp_lobby") && catalog.AllMaps.Contains("mp_rr_canyonlands_mu1"),
            string.Join(", ", catalog.AllMaps));

        c("catalog: each playlist's own map list is kept for the filter",
            catalog.MapsForPlaylist("survival").Count == 1
            && catalog.MapsForPlaylist("survival")[0] == "mp_rr_divided_moon_mu1",
            string.Join(", ", catalog.MapsForPlaylist("survival")));

        // On disk: the install's loose maps are launchable even when no playlist
        // names them, and the debris beside them is not.
        c("catalog: a map on disk joins the list",
            catalog.AllMaps.Contains("mp_rr_fixture_loose"),
            string.Join(", ", catalog.AllMaps));

        c("catalog: a backup, a loadscreen-named file and a navmesh hull are not maps",
            !catalog.AllMaps.Any(m =>
                m.Contains("bak", StringComparison.OrdinalIgnoreCase)
                || m.Contains("loadscreen", StringComparison.OrdinalIgnoreCase)
                || m.Contains("navmesh", StringComparison.OrdinalIgnoreCase)
                || m.EndsWith("_small")),
            string.Join(", ", catalog.AllMaps));

        // Names for maps the def never mentions: the curated file first, and a
        // curated stem the file leaves unnamed is named from the launcher's own
        // strings — which is the whole point of the curated allowlist.
        c("catalog: the install's curated map names are read",
            catalog.MapNames.TryGetValue("mp_rr_fixture_curated", out var curated)
            && curated == "Fixture Curated",
            catalog.MapNames.TryGetValue("mp_rr_fixture_curated", out var cu) ? cu : "(none)");

        // A curated stem the file lists with no name stays *offered* (MapOrder is
        // the allowlist) but carries no name here: upstream's MapNames only holds
        // named stems, and the second pass — map_title.<stem> in the launcher's
        // own strings, applied where a card is built — is what names it. So the
        // honest reading is "in the allowlist, not in this table".
        c("catalog: a curated stem the file leaves unnamed is offered, not named here",
            catalog.MapOrder.Contains("mp_rr_divided_moon_mu1", StringComparer.OrdinalIgnoreCase)
            && !catalog.MapNames.ContainsKey("mp_rr_divided_moon_mu1")
            && MapLabels.PlayerName("mp_rr_divided_moon_mu1", catalog.MapNames) == "Divided Moon Mu1",
            $"curated={catalog.MapOrder.Count}, "
            + $"label='{MapLabels.PlayerName("mp_rr_divided_moon_mu1", catalog.MapNames)}'");

        // An install that is not there at all: no throw, nothing invented.
        var missing = PlaylistCatalogLoader.Load(
            Path.Combine(root, "no-such-install"), "english", false, UiLoc);
        c("catalog: an install that is not there is an empty catalog, not an error",
            missing.SourcePath is null && missing.Entries.Count == 0
            && missing.AllMaps.Count == 0 && missing.HostPlaylists.Count == 0,
            $"{missing.Entries.Count} entr(ies), {missing.AllMaps.Count} map(s)");

        // A def with no localization file beside it still name-resolves through
        // uiLoc, which is what the launcher ships for the modes it knows.
        var noLoc = Path.Combine(root, "no-loc");
        Directory.CreateDirectory(Path.Combine(noLoc, "platform"));
        File.Copy(
            Path.Combine(root, "platform", "playlists_r5_patch.txt"),
            Path.Combine(noLoc, "platform", "playlists_r5_patch.txt"));
        var bare = PlaylistCatalogLoader.Load(noLoc, "english", false, UiLoc);
        c("catalog: without the install's localization the launcher's own names still apply",
            bare.Entries.Count == 3
            && bare.Entries.First(e => e.Id == "lobby").Title == "Lobby"
            && bare.Entries.First(e => e.Id == "survival_dev").Title == "survival_dev",
            $"lobby='{bare.Entries.First(e => e.Id == "lobby").Title}', "
            + $"survival_dev='{bare.Entries.First(e => e.Id == "survival_dev").Title}' — "
            + "the playlist's own id, which is upstream's fallback when a def's loc is missing");
    }

    // ---------------------------------------------------------------- wiring

    static void WiringChecks(Action<string, bool, string> c, MainViewModel vm, string root)
    {
        var install = vm.InstallPath;
        var map = vm.Map;

        try
        {
            vm.InstallPath = root;
            var done = false;
            _ = vm.ReloadCatalogAsync().ContinueWith(_ => done = true, TaskScheduler.Default);
            Pump(() => done, 5000);

            c("catalog: the reload button's path is what fills the map combo",
                done && vm.MapIds.Count >= 3,
                done ? string.Join(", ", vm.MapIds) : "the reload did not finish");

            c("catalog: the selected map survives a reload when the catalog still knows it",
                vm.MapIds.Contains(vm.Map, StringComparer.OrdinalIgnoreCase),
                $"map='{vm.Map}'");

            c("catalog: the labels the browser and the boards use come from the same catalog",
                vm.CatalogMapNames.TryGetValue("mp_rr_fixture_curated", out var named)
                && named == "Fixture Curated",
                vm.CatalogMapNames.TryGetValue("mp_rr_fixture_curated", out var n) ? n : "(none)");

            // ------------------------------------------------ the mode cards
            //
            // The rail, the hero and the map grid all read the card list, so
            // these are relationship checks: whatever the fixture's def declares,
            // the three views of it have to agree.

            c("catalog: every card is a family the install declares, or the synthesized lobby",
                vm.ModeCards.Count > 0
                && vm.ModeCards.All(card => vm.Catalog.FindFamily(card.Id) is not null
                                            || ModeCardViewModel.IsLobbyPlaylist(card.Id)),
                $"{vm.ModeCards.Count} card(s) from {vm.Catalog.Families.Count} family(ies): "
                + string.Join(", ", vm.ModeCards.Select(x => $"{x.Id}({x.Maps.Count})")));

            c("catalog: the rail renders exactly the cards, in their groups",
                vm.Modes.SelectMany(g => g.Items).Count() == vm.ModeCards.Count
                && vm.Modes.All(g => g.Items.Count > 0)
                && vm.Modes.SelectMany(g => g.Items).All(i => vm.ModeCards.Contains(i.Card)),
                $"{vm.Modes.Count} group(s): " + string.Join(", ", vm.Modes.Select(g => $"{g.Header}{g.Items.Count}")));

            c("catalog: a card the install declares carries the maps its playlist offers",
                vm.ModeCards.Any(x => !ModeCardViewModel.IsLobbyPlaylist(x.Id) && x.Maps.Count > 0),
                string.Join("; ", vm.ModeCards.Where(x => x.Maps.Count > 0)
                    .Select(x => $"{x.Id}: {string.Join("+", x.Maps.Select(m => m.Stem))}")));

            c("catalog: a pinned mode offers its one map and no picker",
                vm.ModeCards.Any(x => x.MapIsPinned && x.Maps.Count == 1 && x.MapCountLabel == "·"),
                string.Join(", ", vm.ModeCards.Where(x => x.MapIsPinned)
                    .Select(x => $"{x.Id}={x.SelectedMapStem}")));

            // The selected card is the one owner of the grid and the hero, so
            // both have to follow the selection rather than the other way round.
            var other = vm.ModeCards.FirstOrDefault(x => !ReferenceEquals(x, vm.SelectedMode));
            if (other is not null)
            {
                vm.SelectModeCard(other, persist: false);
                c("catalog: selecting a card moves the grid, the hero and the settings key",
                    ReferenceEquals(vm.SelectedMode, other) && other.IsSelected
                    && vm.MapTiles.Count == other.Maps.Count
                    && vm.MapTiles.All(t => other.Maps.Any(m => ReferenceEquals(m, t.Option)))
                    && vm.HeroTitle == other.Title
                    && vm.HeroCommand.Contains("+launchplaylist " + other.PlaylistId, StringComparison.Ordinal)
                    && vm.HeroCommand.Contains("+map " + other.SelectedMapStem, StringComparison.Ordinal),
                    $"'{other.Id}': {vm.MapTiles.Count} tile(s), '{vm.HeroCommand}'");

                // The tile click is the same rule one step further in: the mode
                // keeps the map you picked. This calls the port of OnMapTileClick
                // with persist off -- the click would otherwise write the fixture's
                // family ids and map stems into the player's own settings file,
                // and the store's round-trip is checked for itself below.
                if (other.Maps.Count > 1)
                {
                    var pick = other.Maps[1];
                    vm.SelectedMode!.SelectedMap = pick;
                    c("catalog: the map a mode is left on follows it into the hero",
                        other.SelectedMapStem == pick.Stem
                        && vm.HeroCommand.Contains("+map " + pick.Stem, StringComparison.Ordinal)
                        && vm.SelectedPlayPair().Map == pick.Stem,
                        $"{pick.Stem} / '{vm.HeroCommand}'");
                }
            }

            c("catalog: the map a family was last played on is remembered, case-insensitively",
                RememberedMapRoundTrip(),
                "survival_dev -> mp_rr_fixture_loose survives JSON, lookup ignores case");

            // Back to a real (empty) state, so no later check reads a fixture map.
            vm.InstallPath = install;
            var restored = false;
            _ = vm.ReloadCatalogAsync().ContinueWith(_ => restored = true, TaskScheduler.Default);
            Pump(() => restored, 5000);
            vm.Map = map;
            c("catalog: the fixture install leaves nothing behind",
                restored && !vm.MapIds.Contains("mp_rr_fixture_loose"),
                string.Join(", ", vm.MapIds));
        }
        finally
        {
            vm.InstallPath = install;
            vm.Map = map;
        }
    }

    /// <summary>
    /// SettingsStore.ModeMaps, ported as JSON: the map a family was last played
    /// on survives a save/load cycle and the lookup ignores case, which is what
    /// the registry form's key comparison did. Checked on a throwaway instance --
    /// the suite never writes the player's own settings file.
    /// </summary>
    static bool RememberedMapRoundTrip()
    {
        var s = new LinuxSettings();
        s.RememberMap("survival_dev", " mp_rr_fixture_loose ");
        s.LastModePlaylist = "survival_dev";

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<LinuxSettings>(json);

        return back is not null
            && back.ModeMaps.Count == 1
            && back.LastModePlaylist == "survival_dev"
            && back.RememberedMap("SURVIVAL_DEV") == "mp_rr_fixture_loose"
            && back.RememberedMap("nope") is null;
    }

    /// <summary>
    /// The catalog is read off the UI thread (the port's one deliberate divergence
    /// from Windows' synchronous reload), so a check that waits for it has to let
    /// the dispatcher run: the continuation comes back here, and nothing arrives
    /// until the jobs are pumped.
    /// </summary>
    static void Pump(Func<bool> done, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------- real install

    /// <summary>
    /// The install itself, when it is there: this is the only fixture that was
    /// not written by this file. The platform lane is on disk here, so the def,
    /// the localization file and the curated map list are the real ones — the
    /// check is that the ported parser reads them into a catalog the launcher can
    /// actually use: modes offered, maps that exist, names that resolve.
    ///
    /// Read-only, by design: `--selftest` reads the player's install and never
    /// writes to it, and never needs it (the fixture pass above is what runs on a
    /// machine with no install).
    /// </summary>
    static void RealInstallChecks(Action<string, bool, string> c, MainViewModel vm)
    {
        var defPath = DefPathOf(vm.InstallPath);
        if (defPath is null)
        {
            c("catalog: the real install's own def parses",
                true,
                $"not checked: no install at {vm.InstallPath}");
            return;
        }

        var catalog = PlaylistCatalogLoader.Load(
            vm.InstallPath, "english", vm.ShowUnlistedMaps, Loc.Lookup);

        c("catalog: the real install's own def parses",
            catalog.Entries.Count > 0 && catalog.AllMaps.Count > 0,
            $"{catalog.Entries.Count} entr(ies), {catalog.AllMaps.Count} map(s), "
            + $"{catalog.Modes.Count} mode(s) from {Path.GetFileName(catalog.SourcePath ?? "?")}");

        c("catalog: the real install's modes are the ones its own vars mark",
            catalog.Modes.Count > 0 && catalog.Modes.All(m => m.IsMode),
            string.Join(", ", catalog.Modes.Take(8).Select(m => m.Id))
            + (catalog.Modes.Count > 8 ? " …" : ""));

        c("catalog: every mode card the rail would show has maps behind it",
            catalog.Families.Count > 0 && catalog.Families.All(f => f.Variants.Count > 0),
            $"{catalog.Families.Count} famil(ies), maps "
            + string.Join(", ", catalog.Families.Take(3).Select(f => $"{f.Id}:{f.Variants.Count}")));

        // The card layer itself, off the real def: these are the chips this
        // machine's rail would show. The pair matters most — it is what Simple
        // mode's Play runs — and a card with no title or a map the install does
        // not have would be a dead chip.
        var cards = catalog.Families
            .Select(f => ModeCardViewModel.FromFamily(f, catalog.MapNames, null))
            .Where(card => card.Maps.Count > 0)
            .ToList();
        var dead = cards.Where(card =>
                string.IsNullOrWhiteSpace(card.Title)
                || string.IsNullOrWhiteSpace(card.PlaylistId)
                || string.IsNullOrWhiteSpace(card.SelectedMapStem)
                || !catalog.AllMaps.Contains(card.SelectedMapStem, StringComparer.OrdinalIgnoreCase))
            .ToList();

        c("catalog: every real card is launchable — a title, a playlist, and a map the install has",
            cards.Count > 0 && dead.Count == 0,
            dead.Count == 0
                ? $"{cards.Count} card(s), e.g. " + string.Join(" / ", cards.Take(3).Select(x =>
                      $"{x.Title} -> +launchplaylist {x.PlaylistId} +map {x.SelectedMapStem}"))
                : $"{dead.Count} dead card(s): " + string.Join(", ", dead.Take(3).Select(x => x.Id)));

        c("catalog: the real cards land in the rail's own groups, and every group is titled",
            cards.All(x => ModeGroups.Order.Contains(x.Group)) && cards.All(x => x.GroupTitle.Length > 0),
            string.Join(", ", cards.GroupBy(x => x.GroupTitle).Select(g => $"{g.Key}={g.Count()}")));

        // A mode the launcher ships a title for must come out titled, and a mode
        // it does not must fall back to the install's own name — never to an empty
        // card.
        c("catalog: no offered mode or map is left without a label",
            catalog.HostPlaylists.All(e => e.Title.Length > 0 && e.DisplayName.Length > 0)
            && catalog.AllMaps.All(m => MapLabels.PlayerName(m, catalog.MapNames).Length > 0),
            catalog.HostPlaylists.Count(e => e.Title.Length == 0) + " unlabeled mode(s)");

        // The curated list is the allowlist: stems in it must be on disk or in a
        // playlist, and every name in it must be a name.
        c("catalog: the curated map list is read, in its own order",
            catalog.MapOrder.Count > 0
            && catalog.MapNames.Count > 0
            && catalog.MapOrder.All(m => catalog.MapNames.ContainsKey(m)),
            $"{catalog.MapOrder.Count} curated stem(s), {catalog.MapNames.Count} name(s)");

        // The choices a mode offers are the intersection the picker needs: curated,
        // on disk, and never a map that already has its own button.
        var firstMode = catalog.Modes.FirstOrDefault(m => string.IsNullOrEmpty(m.PinnedMap));
        var choices = firstMode is null
            ? Array.Empty<string>()
            : catalog.MapChoicesForMode(firstMode.Id);
        c("catalog: a mode's map choices are the maps its own block declares",
            firstMode is null || choices.Count > 0,
            firstMode is null
                ? "every mode pins its map"
                : $"{firstMode.Id}: {choices.Count} choice(s), e.g. {string.Join(", ", choices.Take(4))}");
    }

    // --------------------------------------------------------------- fixture

    /// <summary>
    /// What the launcher's own strings say, the way the shell's Loc answers:
    /// `mode_title.<id>` / `map_title.<stem>` / the picker tips. Only the keys the
    /// fixture's def does not carry itself are needed here.
    /// </summary>
    static string? UiLoc(string key) => key switch
    {
        "mode_title.lobby" => "Lobby",
        "mode_blurb.lobby" => "S21 offline lobby.",
        "map_title.mp_rr_divided_moon_mu1" => "Broken Moon",
        "map_title.mp_rr_canyonlands_mu1" => "Kings Canyon",
        "map_title.mp_rr_fixture_loose" => "Fixture Loose (launcher)",
        "map_title.mp_lobby" => "Lobby",
        _ => null,
    };

    static void WriteFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "platform", "localization"));
        Directory.CreateDirectory(Path.Combine(root, "platform", "maps", "navmesh"));
        Directory.CreateDirectory(Path.Combine(root, "maps", "loadscreen"));

        // The def: a mode with an inherited parent, a plain playlist, and a pinned
        // lobby. Same shape as the shipped file, trimmed to three entries.
        File.WriteAllText(Path.Combine(root, "platform", "playlists_r5_patch.txt"), """
            Playlists
            {
                survival_dev
                {
                    inherit survival
                    maps
                    {
                        mp_rr_divided_moon_mu1 1
                        mp_rr_canyonlands_mu1 1
                    }
                    vars
                    {
                        name #PL_FREE_ROAM
                        r5f_mode 1
                        r5f_mode_order 10
                        r5f_mode_blurb #PL_BLURB_FREE
                    }
                }
                survival
                {
                    maps { mp_rr_divided_moon_mu1 1 }
                    vars { name "Survival" }
                }
                lobby
                {
                    maps { mp_lobby 1 }
                    vars
                    {
                        name "Lobby"
                        r5f_mode 1
                        r5f_mode_order 99
                        r5f_mode_map mp_lobby
                    }
                }
            }
            """);

        // The install's localization file: "key" "value", the key being the rtech
        // hash of the token the def wrote.
        var loc = new StringBuilder();
        loc.Append('"').Append(RtechHash.LocKeyFromToken("#PL_FREE_ROAM")).Append("\" \"Free Roam\"\n");
        loc.Append('"').Append(RtechHash.LocKeyFromToken("#PL_BLURB_FREE"))
           .Append("\" \"Explore any map. No ring, no match, no timer.\"\n");
        File.WriteAllText(
            Path.Combine(root, "platform", "localization", "localization_english.txt"),
            loc.ToString());

        // The curated names: one stem the file names itself, one it lists with no
        // name so the launcher's own strings supply it, and one map that is only
        // on disk (named by the file, since nothing else would name it).
        File.WriteAllText(Path.Combine(root, "platform", "r5f_map_names.txt"), """
            # stem = player-facing name
            mp_rr_fixture_loose = Fixture Loose
            mp_rr_fixture_curated = Fixture Curated
            mp_rr_divided_moon_mu1 =
            """);

        void Touch(params string[] parts) =>
            File.WriteAllText(Path.Combine(root, Path.Combine(parts)), "fixture");

        Touch("maps", "mp_rr_fixture_loose.bsp");
        Touch("maps", "mp_rr_fixture_loose.bak");
        // Rejected by name, which is upstream's rule ("loadscreen" in the file
        // name); the same word as a *directory* is not rejected, and that is
        // upstream's behaviour too — the file is copied verbatim.
        Touch("maps", "mp_rr_fixture_loadscreen.bsp");
        Touch("platform", "maps", "navmesh", "mp_rr_fixture_loose_small.nm");
    }
}
