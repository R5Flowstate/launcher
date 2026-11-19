using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Checks for the Patch Notes tab. The bug this replaced was silent: the old
/// reader looked for a "notes" array while every NOTES.json on the CDN and in
/// the bundle ships "entries", so the tab always fell back to a hardcoded stub
/// and nobody noticed. These checks pin the wire key, the bundled copies, the
/// source precedence, the offline cache and the unread stamp.
///
/// Nothing here touches the network or the player's settings file. The unread
/// decision is checked through a pure function rather than by writing
/// NotesSeenStamp, and the source rules run through the local-path lane plus one
/// deliberate allowlist failure that lands in the cache-fallback branch.
/// </summary>
static class NotesSelfTest
{
    public static void Run(Action<string, bool, string> check, MainViewModel vm)
    {
        WireShapeChecks(check);
        BundledChecks(check, vm);
        SourceChecks(check);
        LineChecks(check);
        TabChecks(check, vm);
    }

    // ------------------------------------------------------------- wire shape

    static void WireShapeChecks(Action<string, bool, string> check)
    {
        var dir = NewTempDir("wire");
        try
        {
            // The exact shape the CDN serves (and the reason for the old bug).
            var good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, """
                {"schema":1,"kind":"notes","entries":[
                  {"date":"15 Sep 2026","title":"1.0.6","items":["Fix crash","## Audio","Louder footsteps"]}
                ]}
                """);
            var doc = NotesDocumentIO.Load(good);
            Check(check, "notes: the wire key is 'entries'", doc.Entries.Count == 1,
                $"{doc.Entries.Count} entries");
            Check(check, "notes: entry date/title/items survive",
                doc.Entries.Count == 1
                && doc.Entries[0].Title == "1.0.6"
                && doc.Entries[0].Date == "15 Sep 2026"
                && doc.Entries[0].Items.Count == 3);

            // The old Linux DTO's key. It must yield nothing, so a future
            // regression to "notes" fails loudly here instead of silently
            // showing the placeholder.
            var wrong = Path.Combine(dir, "wrong.json");
            File.WriteAllText(wrong, """
                {"schema":1,"notes":[{"date":"15 Sep 2026","title":"1.0.6","items":["x"]}]}
                """);
            Check(check, "notes: the old 'notes' key is not the contract",
                NotesDocumentIO.Load(wrong).Entries.Count == 0);

            // The reader itself is strict — every NotesSource path is what makes
            // a bad body non-fatal, which is why each one is wrapped.
            Check(check, "notes: the raw reader throws on a non-JSON body",
                Throws(() => NotesDocumentIO.Load(Body(dir, "broken.json", "<html>404</html>"))));
            Check(check, "notes: the raw reader throws on a missing file",
                Throws(() => NotesDocumentIO.Load(Path.Combine(dir, "absent.json"))));

            // The cache is written with this serializer, so it has to round-trip.
            var round = Path.Combine(dir, "round.json");
            NotesDocumentIO.Save(round, doc);
            var back = NotesDocumentIO.Load(round);
            Check(check, "notes: the cache format round-trips",
                back.Entries.Count == 1 && back.Entries[0].Items.Count == 3);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ---------------------------------------------------------------- bundled

    static void BundledChecks(Action<string, bool, string> check, MainViewModel vm)
    {
        // An empty cache dir forces the bundled fallback, which is what a
        // first run and an offline start both see.
        var cache = NewTempDir("bundled");
        try
        {
            var game = NotesSource.LoadGameCachedOrBundled(cache);
            Check(check, "bundled: the game notes ship with the launcher", game.Count >= 20,
                $"{game.Count} entries");
            Check(check, "bundled: every game entry has a title and at least one line",
                game.Count > 0 && game.All(e =>
                    !string.IsNullOrWhiteSpace(e.Title) && e.Items is { Count: > 0 }));

            var launcher = NotesSource.LoadLauncherCachedOrBundled(cache);
            Check(check, "bundled: the launcher changelog ships too", launcher.Count >= 20,
                $"{launcher.Count} entries");
            Check(check, "bundled: launcher entries are launcher versions",
                launcher.Count > 0
                && launcher.Count(e => e.Title.StartsWith("Launcher ", StringComparison.Ordinal)) >= 20,
                launcher.Count > 0 ? launcher[0].Title : "none");

            // The launcher's own page must describe this build, otherwise the
            // unread dot and the auto-select can never fire on Linux.
            var needle = "Launcher " + vm.Version;
            Check(check, $"bundled: a changelog entry exists for {needle}",
                launcher.Any(e => e.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0),
                launcher.Count > 0 ? launcher[0].Title : "none");
        }
        finally
        {
            Cleanup(cache);
        }
    }

    // ------------------------------------------- source precedence and cache
    //
    // The transport is https-only and allowlisted to *.r5flowstate.org, so a
    // loopback server cannot stand in for the CDN here (the live lane is covered
    // by --channel-check, which fetches the real NOTES.json). Every source rule
    // can still be checked offline through the local-path lane, which is also
    // the lane tools/local_channel uses, plus one deliberate allowlist failure
    // that lands in the cache-fallback branch.

    /// <summary>A channel URL that no fetch can succeed against, so the loader
    /// falls through to its cache-then-bundled tail. 127.0.0.1 is https but not
    /// on the allowlist, so this fails locally without touching the network.</summary>
    const string UnreachableChannelUrl = "https://127.0.0.1:1/channel/CHANNEL.json";

    static void SourceChecks(Action<string, bool, string> check)
    {
        const string siblingBody = """
            {"schema":1,"kind":"notes","entries":[
              {"date":"21 Sep 2026","title":"1.1.0 - hotfix 3","items":["## Fixes","Crash on respawn"]},
              {"date":"20 Sep 2026","title":"1.1.0","items":["New map"]}
            ]}
            """;
        const string channelBody = """
            {"schema":1,"kind":"notes","entries":[
              {"date":"22 Sep 2026","title":"from-channel-notes-url","items":["channel.notes_url points here"]}
            ]}
            """;
        const string envBody = """
            {"schema":1,"kind":"notes","entries":[
              {"date":"22 Sep 2026","title":"from-env","items":["Only the env var points here"]}
            ]}
            """;
        const string cacheBody = """
            {"schema":1,"kind":"notes","entries":[
              {"date":"19 Sep 2026","title":"cached-notes","items":["Last good copy from an earlier run"]}
            ]}
            """;

        var dir = NewTempDir("source");
        var cache = Path.Combine(dir, "cache");
        var channelDir = Path.Combine(dir, "channel");
        var cached = Path.Combine(cache, "r5f-notes", "game-NOTES.json");
        var priorEnv = Environment.GetEnvironmentVariable(ChannelSource.NotesUrlEnvVar);
        try
        {
            Directory.CreateDirectory(channelDir);
            var channelUrl = Body(channelDir, "CHANNEL.json", """{"channel":"test"}""");
            Body(channelDir, "NOTES.json", siblingBody);
            var notesUrlPath = Body(dir, "channel-notes.json", channelBody);
            var envPath = Body(dir, "env-notes.json", envBody);

            using var fetcher = new FileSystemFetcher(TimeSpan.FromSeconds(10));

            // 1. The notes sit next to the channel manifest — the CDN layout, and
            //    the local-channel layout too.
            Environment.SetEnvironmentVariable(ChannelSource.NotesUrlEnvVar, null);
            var sibling = Run(() => NotesSource.LoadAsync(null, channelUrl, fetcher, cache));
            Check(check, "source: NOTES.json is found next to CHANNEL.json",
                sibling.Count == 2 && sibling[0].Title == "1.1.0 - hotfix 3",
                sibling.Count > 0 ? sibling[0].Title : "none");

            // 2. channel.notes_url wins over the sibling.
            var channelManifest = new ChannelManifest
            {
                Channel = "test",
                BaseUrl = "https://cdn.r5flowstate.org/channel/",
                NotesUrl = notesUrlPath,
            };
            var fromChannel = Run(() => NotesSource.LoadAsync(
                channelManifest, channelUrl, fetcher, cache));
            Check(check, "source: channel.notes_url wins over the sibling",
                fromChannel.Count == 1 && fromChannel[0].Title == "from-channel-notes-url",
                fromChannel.Count > 0 ? fromChannel[0].Title : "none");

            // 3. R5F_NOTES_URL wins over everything (the override lane).
            Environment.SetEnvironmentVariable(ChannelSource.NotesUrlEnvVar, envPath);
            var fromEnv = Run(() => NotesSource.LoadAsync(channelManifest, channelUrl, fetcher, cache));
            Check(check, "source: R5F_NOTES_URL wins over channel.notes_url",
                fromEnv.Count == 1 && fromEnv[0].Title == "from-env",
                fromEnv.Count > 0 ? fromEnv[0].Title : "none");

            // 4. A fetch that cannot succeed still leaves the tab populated: the
            //    last good copy is used before the bundled one.
            Environment.SetEnvironmentVariable(ChannelSource.NotesUrlEnvVar, null);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllText(cached, cacheBody);
            var offline = Run(() => NotesSource.LoadAsync(
                null, UnreachableChannelUrl, fetcher, cache));
            Check(check, "source: an unreachable channel falls back to the cached notes",
                offline.Count == 1 && offline[0].Title == "cached-notes",
                offline.Count > 0 ? offline[0].Title : "none");

            // 5. A corrupt cache is skipped in favour of the bundled copy.
            File.WriteAllText(cached, "{ this is not json");
            var afterCorrupt = Run(() => NotesSource.LoadAsync(
                null, UnreachableChannelUrl, fetcher, cache));
            Check(check, "source: a corrupt cache falls back to the bundled notes",
                afterCorrupt.Count >= 20, $"{afterCorrupt.Count} entries");

            // 6. No cache at all: the bundled copy, which is why a first run and
            //    an offline start both show notes.
            File.Delete(cached);
            var bundled = Run(() => NotesSource.LoadAsync(
                null, UnreachableChannelUrl, fetcher, cache));
            Check(check, "source: with no cache the bundled notes are used",
                bundled.Count >= 20, $"{bundled.Count} entries");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChannelSource.NotesUrlEnvVar, priorEnv);
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------ lines

    static void LineChecks(Action<string, bool, string> check)
    {
        var header = PatchNoteLineVM.Parse("## Fixes");
        Check(check, "line: '## ' makes a category header", header.IsHeader && header.Text == "Fixes",
            $"header={header.IsHeader} text='{header.Text}'");

        var bullet = PatchNoteLineVM.Parse("- Crash on respawn");
        Check(check, "line: anything else stays as written",
            !bullet.IsHeader && bullet.Text == "- Crash on respawn", bullet.Text);

        var empty = PatchNoteLineVM.Parse(null);
        Check(check, "line: a null line is empty, not a crash",
            !empty.IsHeader && empty.Text.Length == 0);
    }

    // -------------------------------------------------------------------- tab

    static void TabChecks(Action<string, bool, string> check, MainViewModel vm)
    {
        vm.BindNotesLocal();

        Check(check, "tab: the bundled notes reach the list", vm.NotesEntries.Count >= 20,
            $"{vm.NotesEntries.Count} rows");
        Check(check, "tab: the list is real patch notes, not the placeholder",
            vm.NotesEntries.Count > 0 && vm.NotesEntries[0].Title != "Patch notes",
            vm.NotesEntries.Count > 0 ? vm.NotesEntries[0].Title : "none");
        Check(check, "tab: a game entry with a '## ' line renders as a header",
            vm.NotesEntries.Any(r => r.Items.Any(i => i.IsHeader)),
            $"{vm.NotesEntries.Sum(r => r.Items.Count(i => i.IsHeader))} header line(s)");
        Check(check, "tab: a blank date becomes an em dash and a blank title 'Notes'",
            vm.NotesEntries.All(r => r.Date.Length > 0 && r.Title.Length > 0));

        // Both pages are driven from the same list, so switching must swap it.
        vm.ShowNotesLauncher();
        var launcherTop = vm.NotesEntries.FirstOrDefault()?.Title ?? "";
        Check(check, "tab: the Launcher page shows the launcher changelog",
            launcherTop.StartsWith("Launcher ", StringComparison.Ordinal), launcherTop);
        vm.ShowNotesGame();
        var gameTop = vm.NotesEntries.FirstOrDefault()?.Title ?? "";
        Check(check, "tab: switching back restores the game notes",
            gameTop != launcherTop && gameTop.Length > 0, gameTop);

        // The unread dot: pure logic, checked without touching the player's
        // settings file. A new fingerprint lights it; the stored one clears it.
        var stamp = vm.CurrentNotesStamp();
        Check(check, "unread: the fingerprint covers both pages",
            stamp.StartsWith("g\t", StringComparison.Ordinal)
            && stamp.Contains("\nl\t", StringComparison.Ordinal), stamp[..Math.Min(24, stamp.Length)]);
        Check(check, "unread: the notes just read are not unread",
            !MainViewModel.IsNotesUnread(stamp, stamp));
        Check(check, "unread: a new entry lights the dot",
            MainViewModel.IsNotesUnread("g\t15 Sep 2026\t1.0.6\n", stamp));
        Check(check, "unread: an empty list is never unread",
            !MainViewModel.IsNotesUnread(null, "") && !MainViewModel.IsNotesUnread("anything", ""));
        Check(check, "unread: the dot agrees with the stored stamp",
            vm.NotesUnread == MainViewModel.IsNotesUnread(vm.NotesSeenStamp, stamp),
            $"dot={vm.NotesUnread}");
    }

    // ---------------------------------------------------------------- helpers

    static void Check(Action<string, bool, string> check, string name, bool ok, string detail = "")
        => check(name, ok, detail);

    static List<NotesEntry> Run(Func<Task<List<NotesEntry>>> f)
    {
        try
        {
            return f().GetAwaiter().GetResult();
        }
        catch
        {
            return new List<NotesEntry>();
        }
    }

    static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch
        {
            return true;
        }
    }

    static string Body(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"r5f-notes-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
