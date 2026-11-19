using System.Diagnostics;
using R5Flowstate.Content.Rpak;

namespace R5Flowstate.Linux.Core;

/// <summary>What the launcher asked the art host for, in one value. Stems are map
/// loadscreens named by stem; paks are standalone art loadscreens named by path
/// (the lobby's lane). <paramref name="Force"/> re-decodes what is already cached.</summary>
public sealed record ArtRequest(
    string ProtonDir,
    string InstallRoot,
    int Width,
    IReadOnlyList<string>? Stems = null,
    IReadOnlyList<string>? Paks = null,
    bool Force = false,
    Action<string>? Log = null);

/// <summary>One asked-for picture's outcome: the cache file to read, or the
/// decoder's own reason for there being none. The reason is carried and not
/// flattened — "no loadscreen pak" and "that pak is not Oodle-decodable" are
/// different answers and the log says which one it was.</summary>
public sealed record ArtOutcome(
    string Kind,
    string Input,
    string CachePath,
    bool Ok,
    string Reason,
    string Source)
{
    public static ArtOutcome Missing(string kind, string input, string reason)
        => new(kind, input, string.Empty, false, reason, string.Empty);
}

/// <summary>One batch's answer: what came back, whether the host ran at all, and
/// the records file that says why when something did not.</summary>
public sealed record ArtBatch(
    IReadOnlyList<ArtOutcome> Outcomes,
    bool Ran,
    int ExitCode,
    string Note,
    string RecordsPath)
{
    public static ArtBatch Skipped(string note, string recordsPath = "")
        => new(Array.Empty<ArtOutcome>(), false, 0, note, recordsPath);

    public int Decoded
    {
        get
        {
            var n = 0;
            foreach (var o in Outcomes)
            {
                if (o.Ok) n++;
            }
            return n;
        }
    }

    /// <summary>The outcome for one request, or null when it was not asked for.</summary>
    public ArtOutcome? For(string kind, string input)
    {
        foreach (var o in Outcomes)
        {
            if (string.Equals(o.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(o.Input, input, StringComparison.Ordinal))
                return o;
        }
        return null;
    }
}

/// <summary>
/// Retail loadscreens, decoded by the Windows art host inside the prefix.
///
/// <para><b>Why a second process.</b> The paks are Oodle-encoded and the decoder
/// is <c>oo2core_8_win64.dll</c>, a PE DLL a native Linux process cannot load —
/// glibc's <c>dlopen</c> answers "invalid ELF header", and that is not a matter of
/// finding the right flags. So the decode happens where the DLL does load:
/// <c>r5f-arthost.exe</c>, run through <see cref="ProtonLauncher"/> in a compatdata
/// of its own, writing pixels into <see cref="CacheDir"/> on the real filesystem
/// through Wine's <c>Z:</c> drive. The launcher reads them back from the same
/// directory, so the pixels never cross a pipe.</para>
///
/// <para><b>Why the answer is a file, not a stream.</b> <c>proton run</c> swallows
/// its child's stdout: a Proton-launched helper's output never reaches the parent
/// while its exit code propagates normally. Measured three independent ways (see
/// the art host's own contract note). The host therefore reports to
/// <c>--records &lt;file&gt;</c>, and this class reads that file. A batch whose exit
/// code is meaningless — a host killed at timeout, a decode that died on a bad
/// block table — still says how far it got.</para>
///
/// <para><b>Its own compatdata, deliberately</b> (<c>~/.local/share/r5flowstate/
/// art-prefix</c>): the art host is not the game and must never be the reason a
/// player's game prefix gets written to. Joining the game's prefix would put a
/// decoder's prefix upgrades on the same critical path as a 45 GB install.</para>
///
/// <para>Nothing here throws on a missing host, a missing Proton build or a
/// refused prefix: each of those is one log line and no art, which is what the
/// launcher did before any of this existed.</para>
/// </summary>
public static class ArtDecode
{
    public const string HostExeName = "r5f-arthost.exe";

    /// <summary>How long one batch may take before it is killed. Generous: the
    /// first run of a fresh compatdata pays Proton's prefix creation (measured:
    /// "from None to 11.0-100" before the host's first line), while a warm batch
    /// of 17 loadscreens took 1.6 s.</summary>
    public const int BatchTimeoutMs = 240_000;

    /// <summary>The batch note when the caller replaced a decode with a newer
    /// request before the host had reported.</summary>
    public const string ReplacedNote = "the loadscreen decode was replaced by a newer request";

    /// <summary>What one of that batch's items says, and it is deliberately not
    /// <see cref="UnreportedReason"/>: "the art host did not report this one" is
    /// the host's silence, which is a decode that failed. This one is a decode the
    /// launcher walked away from, and reading it as the host's fault is how a
    /// superseded batch came to look like a broken decoder.</summary>
    public const string ReplacedReason = "replaced by a newer request before the art host reported it";

    /// <summary>The host's silence about a request that really did reach it.</summary>
    public const string UnreportedReason = "the art host did not report this one";

    /// <summary>What every item of a batch says when the host never wrote a single
    /// record — it did not start at all.
    ///
    /// <para>Distinct from <see cref="UnreportedReason"/> on purpose, and this one is
    /// the difference between a diagnosis and a wall of noise. "Did not report this
    /// one" is per-item silence, which is what a host that answered about some of a
    /// batch and not others leaves behind. A host that wrote nothing did not fail
    /// picture by picture: it died at its first line, and every item saying so
    /// individually is sixty lines that all mean the same thing. The batch note names
    /// the cause once; this is what the items say underneath it.</para></summary>
    public const string NotStartedReason = "the art host did not start";

    /// <summary>Where install.sh puts the host: the <c>winhost/</c> folder beside
    /// the launcher — <see cref="WinHost"/>'s, which holds one folder per helper.</summary>
    public static string HostDir() => WinHost.Dir();

    /// <summary>Records files kept in <see cref="RecordsDir"/>. Small (a line per
    /// picture) and worth having when something failed, but not worth growing
    /// without end.</summary>
    const int RecordsKept = 20;

    /// <summary>The hosted decoder, or null when it is not installed.
    /// <see cref="WinHost.Find"/> holds the search order (beside the launcher, then
    /// the install prefix, then a published build in this source tree), because the
    /// relay is looked up exactly the same way.</summary>
    public static string? FindHost() => WinHost.Find(HostExeName);

    /// <summary>The launcher's own install folder, for a log line that names where
    /// we looked.</summary>
    public static string InstallPrefix() => WinHost.InstallPrefix();

    /// <summary>Move the decode cache, the way <see cref="LinuxSettings.InstallPathEnvVar"/>
    /// moves the install. For a small <c>~/.cache</c>, and — the reason it exists —
    /// so the suite can assert the hot path (everything already decoded, so Proton
    /// is never spawned) against a cache it made itself instead of writing test
    /// files into the player's.</summary>
    public const string CacheRootEnvVar = "R5F_ART_CACHE";

    /// <summary>Where the decoded pictures live. One root, and one folder per
    /// decode width inside it: the cache key is the map stem
    /// (<see cref="ArtCache.KeyForStem"/>), which carries no width, so a 196px
    /// tile and a 960px hero of the same map would otherwise be one file and the
    /// hero would be a thumbnail.</summary>
    public static string CacheRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(CacheRootEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return Path.Combine(Home(), ".cache", "r5flowstate", "art");
    }

    public static string CacheDir(int width) => Path.Combine(CacheRoot(), "w" + width);

    /// <summary>The art host's own prefix — never the game's.</summary>
    public static string CompatDataDir()
        => Path.Combine(Home(), ".local", "share", "r5flowstate", "art-prefix");

    /// <summary>Where the host's records files are written and read.</summary>
    public static string RecordsDir() => Path.Combine(CacheRoot(), "records");

    /// <summary>The cache file a map stem would be read from at this width.</summary>
    public static string CacheFileForStem(int width, string stem)
        => Path.Combine(CacheDir(width), ArtCache.FileNameFor(ArtCache.KeyForStem(stem)));

    public static string CacheFileForPak(int width, string pakPath)
        => Path.Combine(CacheDir(width), ArtCache.FileNameFor(ArtCache.KeyForPak(pakPath)));

    /// <summary>
    /// Decode everything asked for that is not already cached, and answer for all
    /// of it — cached entries included.
    ///
    /// <para>This is the entry point the launcher uses. The distinction from
    /// <see cref="FetchAsync"/> matters: the hero is re-queued on every rail click,
    /// and a batch per click would spawn Proton for pictures that are already on
    /// disk. A cache entry counts only when it is the right version <em>and</em>
    /// the right width, which is why the check is the format's own header rather
    /// than the file's existence.</para>
    /// </summary>
    public static async Task<ArtBatch> EnsureCachedAsync(ArtRequest request, CancellationToken ct = default)
    {
        var stems = Wanted(request.Stems);
        var paks = Wanted(request.Paks);
        if (stems.Count == 0 && paks.Count == 0)
            return ArtBatch.Skipped("nothing asked for");

        var width = request.Width;

        var have = new List<ArtOutcome>();
        var wantStems = new List<string>();
        var wantPaks = new List<string>();

        foreach (var stem in stems)
        {
            var hit = Cached(width, "stem", stem, CacheFileForStem(width, stem));
            if (request.Force || hit is null) wantStems.Add(stem);
            else have.Add(hit);
        }

        foreach (var pak in paks)
        {
            var hit = Cached(width, "pak", pak, CacheFileForPak(width, pak));
            if (request.Force || hit is null) wantPaks.Add(pak);
            else have.Add(hit);
        }

        if (wantStems.Count == 0 && wantPaks.Count == 0)
            return new ArtBatch(have, false, 0, "already decoded", string.Empty);

        var batch = await FetchAsync(request with { Stems = wantStems, Paks = wantPaks }, ct)
            .ConfigureAwait(false);

        var all = new List<ArtOutcome>(have.Count + batch.Outcomes.Count);
        all.AddRange(have);
        all.AddRange(batch.Outcomes);
        return batch with { Outcomes = all };
    }

    /// <summary>
    /// One run of the art host, for exactly what was asked for.
    ///
    /// <para>The argv's paths are <c>Z:</c>-mapped (<see cref="PrefixLayout.ToZDrive"/>)
    /// because the host is a Windows process: it reads the install, writes the cache
    /// and writes its records through the same drive, which Wine maps to <c>/</c>.
    /// That mapping is what makes the pixels come back without a copy.</para>
    /// </summary>
    public static async Task<ArtBatch> FetchAsync(ArtRequest request, CancellationToken ct = default)
    {
        var stems = Wanted(request.Stems);
        var paks = Wanted(request.Paks);
        var records = NewRecordsPath();

        if (stems.Count == 0 && paks.Count == 0)
            return ArtBatch.Skipped("nothing asked for", records);

        // Asked for and already abandoned: starting Proton anyway would be a
        // decode nobody is waiting for, in a prefix the request that replaced this
        // one is about to want.
        if (ct.IsCancellationRequested)
            return ArtBatch.Skipped(ReplacedNote, records);

        var host = FindHost();
        if (host is null)
        {
            return ArtBatch.Skipped(
                $"the art host is not installed ({HostDir()}/{HostExeName}) — retail loadscreens "
                + "are Oodle-encoded and cannot be decoded without it", records);
        }

        var proton = (request.ProtonDir ?? string.Empty).Trim();
        if (proton.Length == 0 || !File.Exists(ProtonLauncher.ProtonScriptFor(proton)))
        {
            return ArtBatch.Skipped(
                "no Proton build to run the art host through, so no retail loadscreen can be decoded",
                records);
        }

        var root = (request.InstallRoot ?? string.Empty).Trim();
        if (root.Length == 0 || !Directory.Exists(root))
            return ArtBatch.Skipped("no install to read art from", records);

        var outDir = CacheDir(request.Width);
        try
        {
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(RecordsDir());
        }
        catch (Exception ex)
        {
            return ArtBatch.Skipped($"the art cache directory could not be created: {ex.Message}", records);
        }

        PruneRecords();

        var args = new List<string>
        {
            "--install", PrefixLayout.ToZDrive(root),
            "--out", PrefixLayout.ToZDrive(outDir),
            "--records", PrefixLayout.ToZDrive(records),
            "--width", request.Width.ToString(),
        };
        foreach (var stem in stems)
        {
            args.Add("--stem");
            args.Add(stem);
        }
        foreach (var pak in paks)
        {
            args.Add("--pak");
            args.Add(PrefixLayout.ToZDrive(pak));
        }

        request.Log?.Invoke($"Loadscreen art: decoding {stems.Count + paks.Count} "
            + $"at {request.Width}px through {Path.GetFileName(proton.TrimEnd('/'))}");

        var options = new ProtonLauncher.ProtonRunOptions(
            proton, CompatDataDir(), host, string.Join(' ', args.Select(Quote)));

        var exit = 0;
        var ran = false;
        var note = string.Empty;

        try
        {
            using var child = ProtonLauncher.Start(options);
            ran = true;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(BatchTimeoutMs);
            try
            {
                await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                exit = child.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // Two different things end a batch early, and they are not the same
                // answer.
                //
                // The deadline is a failure: the host is killed, and every item it
                // did not get to says so.
                //
                // A cancelled token is not. It means the caller replaced this
                // request with a newer one — a selection change, a re-roll — and
                // Windows never aborts a running decode for that: its token only
                // stops the *showing*, and the picture is still in hand when the
                // superseded call returns (MainWindow.Loadscreen.cs:571). Here the
                // decode's output is a cache file, so letting it finish is worth
                // more than killing it: the file that lands a second later is
                // exactly the one the next request for that map will ask for. What
                // the kill bought instead was an empty records file and a batch of
                // items reported as the host's silence — a decode that was merely
                // superseded, read as a decoder that failed.
                if (!ct.IsCancellationRequested)
                {
                    Kill(child);
                    var killed = $"the art host did not finish within {BatchTimeoutMs / 1000}s and was killed";
                    return new ArtBatch(Parse(records, request.Width, stems, paks, killed), ran, -1, killed, records);
                }

                return new ArtBatch(
                    Parse(records, request.Width, stems, paks, ReplacedReason), ran, -1, ReplacedNote, records);
            }
        }
        catch (Exception ex)
        {
            // Proton refuses a prefix it does not recognise, and a missing
            // compatdata toolchain is a broken install rather than a crash: both
            // are "no art", said once, in the log the player can read.
            return ArtBatch.Skipped($"the art host could not be started: {ex.Message}", records);
        }

        // A run that left no records file at all did not fail picture by picture:
        // the host writes a `host <version>` line before it looks at a single pak,
        // so nothing on disk means nothing ran — a crash at startup, which under
        // Proton is what a folder holding two different builds' framework files
        // looks like. The items get the one-word answer and the note carries the
        // cause, because sixty copies of "did not report this one" is what this
        // used to look like from the log, with the actual reason nowhere in it.
        var spoke = File.Exists(records);
        var outcomes = Parse(records, request.Width, stems, paks,
            spoke ? null : NotStartedReason);

        if (!spoke)
        {
            note = $"the art host exited {exit} without writing a single record, so it "
                + "never started (records: " + records + "). A winhost folder holding two "
                + "builds' framework files looks exactly like this — re-run install.sh.";
        }
        else if (exit != 0)
        {
            note = $"the art host exited {exit} part-way through its batch"
                + $" (records: {records})";
        }

        return new ArtBatch(outcomes, ran, exit, note, records);
    }

    /// <summary>The cache hit for one item, or null when the file is missing,
    /// truncated, or of another version of the format.
    ///
    /// <para>The width is a sanity check, not an equality: the folder is already
    /// this width's, and the host's <c>--width</c> is a <em>cap</em> — a source
    /// loadscreen narrower than the cap is a normal decode, and demanding an exact
    /// match would re-run Proton for it on every single click.</para>
    /// </summary>
    static ArtOutcome? Cached(int width, string kind, string input, string path)
    {
        if (!ArtCache.TryReadHeader(path, out var header, out _))
            return null;
        if (header.Width <= 0 || header.Width > width)
            return null;

        return new ArtOutcome(kind, input, path, true, string.Empty, "(cached)");
    }

    /// <summary>
    /// The host's records, turned into one outcome per <em>requested</em> item.
    ///
    /// <para>Per request and not per record, deliberately: a host that died
    /// mid-batch leaves records for the items it finished, and an answer that
    /// listed only those would make the rest look like they had never been asked
    /// for. Every request gets a line, and the ones with no record say so.</para>
    ///
    /// <para>The host echoes its own argv back, which is why a request carries the
    /// string it was asked with <em>and</em> the string the host will echo: a pak is
    /// handed over as a <c>Z:</c> path (<see cref="PrefixLayout.ToZDrive"/>) because
    /// the host is a Windows process, so matching on the Linux path it came from
    /// would silently find nothing and read as "the host never reported this one"
    /// while the decoded file sat in the cache. Caught by <c>--art-probe</c> on its
    /// first real run.</para>
    ///
    /// <para><paramref name="unanswered"/> is what an item with no record says. It
    /// exists because "no record" has more than one cause: a host that stayed
    /// silent, and a batch the caller abandoned a moment after starting it (whose
    /// records file is empty for the honest reason that the host has not written
    /// one yet — see <see cref="FetchAsync"/>'s abort path). The default is the
    /// first of those.</para>
    /// </summary>
    /// <remarks>Internal because it is the one piece of this class with no process
    /// and no prefix in it, and because it is where the first real run's bug was: a
    /// pak is echoed back as the <c>Z:</c> path it was handed over as, so a lookup
    /// by the Linux path it came from found nothing. The suite asserts exactly that
    /// round trip.</remarks>
    internal static IReadOnlyList<ArtOutcome> Parse(string recordsPath, int width,
        IReadOnlyList<string> stems, IReadOnlyList<string> paks, string? unanswered = null)
    {
        var ok = new Dictionary<string, (string File, string Source)>(StringComparer.Ordinal);
        var err = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in ReadRecords(recordsPath))
        {
            var f = line.Split('\t');
            if (f.Length < 3)
                continue;

            var kind = f[0];
            if (kind is not ("ok" or "err"))
                continue;

            var what = f[1];
            var input = f[2];
            var key = what + "\t" + input;

            if (kind == "err")
            {
                if (f.Length >= 4) err[key] = f[3];
                continue;
            }

            // ok  kind  input  file  w  h  sourceWidth  sourcePath
            if (f.Length >= 4)
                ok[key] = (f[3], f.Length >= 8 ? f[7] : string.Empty);
        }

        var outcomes = new List<ArtOutcome>(stems.Count + paks.Count);
        foreach (var stem in stems)
            outcomes.Add(One("stem", stem, stem, width, ok, err, unanswered));
        foreach (var pak in paks)
            outcomes.Add(One("pak", pak, PrefixLayout.ToZDrive(pak), width, ok, err, unanswered));
        return outcomes;
    }

    static ArtOutcome One(string kind, string input, string echoed, int width,
        Dictionary<string, (string File, string Source)> ok, Dictionary<string, string> err,
        string? unanswered)
    {
        // Reported against what was asked for, not against what was echoed: the
        // caller looks its outcome up with the path it passed in.
        var key = kind + "\t" + echoed;

        if (ok.TryGetValue(key, out var hit))
        {
            var path = Path.Combine(CacheDir(width), hit.File);
            return new ArtOutcome(kind, input, path, true, string.Empty, hit.Source);
        }

        return ArtOutcome.Missing(kind, input,
            err.TryGetValue(key, out var reason) ? reason : unanswered ?? UnreportedReason);
    }

    /// <summary>A records file's lines. A file the host never managed to create
    /// is an empty list, not an exception — that is exactly the "it died before
    /// it said anything" case this whole channel exists for.</summary>
    static IEnumerable<string> ReadRecords(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
                return Array.Empty<string>();
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return Array.Empty<string>();
        }

        return lines.Where(l => l.Length > 0);
    }

    static string NewRecordsPath()
        => Path.Combine(RecordsDir(), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".tsv");

    /// <summary>Keeps the records folder bounded. Best-effort: a stale records
    /// file is clutter, never a reason to fail a decode.</summary>
    static void PruneRecords()
    {
        try
        {
            var files = new DirectoryInfo(RecordsDir()).GetFiles("*.tsv");
            if (files.Length <= RecordsKept)
                return;

            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (var i = RecordsKept; i < files.Length; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch
        {
        }
    }

    static List<string> Wanted(IReadOnlyList<string>? items)
    {
        var list = new List<string>();
        if (items is null)
            return list;

        foreach (var raw in items)
        {
            var item = (raw ?? string.Empty).Trim();
            if (item.Length > 0 && !list.Contains(item, StringComparer.Ordinal))
                list.Add(item);
        }
        return list;
    }

    /// <summary>One argv element, quoted only when it would otherwise split. The
    /// launcher's own splitter (<see cref="ProtonLauncher"/>'s, which
    /// <c>proton run</c> sees the output of) honours double quotes, and an install
    /// path with a space in it is the normal case on this platform.</summary>
    static string Quote(string arg)
        => arg.Length > 0 && !arg.Any(char.IsWhiteSpace) ? arg : "\"" + arg.Replace("\"", "") + "\"";

    static void Kill(Process child)
    {
        try
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or never ours to kill.
        }
    }

    static string Home()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
