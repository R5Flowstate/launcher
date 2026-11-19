using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R5Flowstate.Content.Rpak;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the loadscreen art lane, and the only way to see art work
/// here without launching a window.
///
///     R5Flowstate --art-probe                 one map: the selected mode's stem, plus one art pak
///     R5Flowstate --art-probe mp_rr_desertlands_hu
///     R5Flowstate --art-probe --stems a,b,c   several, at one width
///     R5Flowstate --art-probe --all           every map loadscreen the install ships, plus one art pak
///     R5Flowstate --art-probe --all --art-paks 8
///     R5Flowstate --art-probe --all --width 196
///     R5Flowstate --art-probe --force         re-decode what is already cached
///
/// <para><c>--all</c> means every <em>map</em> loadscreen — 17 of them in the
/// install this was written against — and not the 263 standalone art sets beside
/// them. A hero decode is ~2 MB of cache each, so "everything" as a default would
/// be half a gigabyte to prove a point; the art sets are counted explicitly with
/// <c>--art-paks</c> because the lobby shuffle only ever shows one at a time.</para>
///
/// <para><b>What "read-only" means here, precisely.</b> It reads the install and
/// the settings file and writes neither. It does write: the decode cache under
/// <c>~/.cache/r5flowstate/art</c>, the host's records under
/// <c>.../records</c>, and the art host's own compatdata under
/// <c>~/.local/share/r5flowstate/art-prefix</c> — because spawning the decoder is
/// the entire point of the lane, and it cannot decode without its prefix. Nothing
/// touches the game's prefix, the game's install, or EA.</para>
///
/// <para>For every item it prints the host's own record, then reads the cache file
/// back and reports the dimensions it actually holds, then builds the PNG the pane
/// would be handed (<see cref="LoadscreenArt.ToPng"/>, the same conversion minus
/// Avalonia's wrapper). A run whose every line says <c>ok</c> and whose every cache
/// file reads back at the right size is art that works; anything else is a reason,
/// which is what the records files exist for.</para>
///
/// <para>Exit codes: <c>0</c> something decoded and every decoded file reads back
/// (a map the install ships no art for is not a failure, and the counts say how
/// many did); <c>2</c> no art host or no Proton build, so nothing could be tried;
/// <c>3</c> the host ran and decoded nothing, or nothing was cached and it did not
/// run; <c>4</c> files decoded but the launcher cannot read them back — the cache
/// is populated with pictures the pane would not show.</para>
/// </summary>
static class ArtProbeCheck
{
    public static async Task<int> RunAsync(string? stem, string? stems, string? installArg,
        string? widthArg, string? artPaksArg, bool all, bool force, bool keep)
    {
        Loc.Initialize(null);

        var settings = LinuxSettings.Load();
        var install = FirstNonEmpty(installArg, settings.InstallPath, LinuxSettings.DefaultInstallPath());
        var proton = ProtonDiscovery.PickLatest(ProtonDiscovery.Discover())?.Dir
                     ?? settings.ProtonDir.Trim();

        var width = int.TryParse(widthArg, out var w) && w > 0 ? w : LoadscreenResolver.DisplayMaxWidth;

        Console.WriteLine("Loadscreen art probe (read-only on your install and settings; it does "
                          + "write the art cache and the art host's own prefix).");
        Console.WriteLine();
        Console.WriteLine($"  host         {ArtDecode.FindHost() ?? "not installed — publish src/R5Flowstate.ArtHost first"}");
        Console.WriteLine($"  host folder  {ArtDecode.HostDir()}");
        Console.WriteLine($"  proton       {(proton.Length == 0 ? "no Proton build found" : proton)}");
        Console.WriteLine($"  install      {install}");
        Console.WriteLine($"  cache        {ArtDecode.CacheDir(width)}  (width {width})");
        Console.WriteLine($"  compatdata   {DescribeCompatData()}");
        Console.WriteLine();

        var mapStems = MapStems(install);
        var artPaks = LoadscreenResolver.ListArtPaks(install);
        Console.WriteLine($"  install has  {mapStems.Count} map loadscreens, {artPaks.Count} standalone art loadscreens");
        Console.WriteLine();

        var wantedStems = new List<string>();
        var wantedPaks = new List<string>();
        var artTake = int.TryParse(artPaksArg, out var asked) && asked > 0 ? asked : 0;

        if (all)
        {
            wantedStems.AddRange(mapStems);
        }
        else
        {
            foreach (var s in Split(stems))
                wantedStems.Add(s);
            if (!string.IsNullOrWhiteSpace(stem))
                wantedStems.Insert(0, stem.Trim());
            if (wantedStems.Count == 0 && artTake == 0)
            {
                // The one the launcher would be showing on a fresh start: the
                // selected mode's map. Deterministic, so two runs compare; and the
                // art pak beside it, because the hero has two lanes and a probe of
                // one of them would not be a probe of the pane.
                wantedStems.Add(string.IsNullOrWhiteSpace(settings.DediMap)
                    ? LinuxSettings.DefaultMap : settings.DediMap.Trim());
                artTake = 1;
            }
        }

        wantedPaks.AddRange(artPaks.Take(artTake));

        if (wantedStems.Count == 0 && wantedPaks.Count == 0)
        {
            Console.WriteLine("Nothing to probe: this install ships no loadscreens at all.");
            Console.WriteLine();
            Console.WriteLine("EXIT 3");
            return 3;
        }

        var host = ArtDecode.FindHost();
        if (host is null || proton.Length == 0)
        {
            Console.WriteLine($"Cannot decode: {(host is null ? "no art host" : "no Proton build")}.");
            Console.WriteLine();
            Console.WriteLine("EXIT 2");
            return 2;
        }

        // What the cache holds before anything runs. This is the launcher's own
        // entry point (EnsureCachedAsync) rather than a raw batch, so a second run
        // of the same command proves the other half of the lane: that a decoded
        // picture is served from disk and Proton is not spawned again. --force is
        // how you ask for the decode to be re-proven instead.
        var cachedBefore = 0;
        foreach (var wanted in wantedStems)
        {
            if (IsCached(ArtDecode.CacheFileForStem(width, wanted))) cachedBefore++;
        }
        foreach (var pak in wantedPaks)
        {
            if (IsCached(ArtDecode.CacheFileForPak(width, pak))) cachedBefore++;
        }
        Console.WriteLine($"  cache has    {cachedBefore}/{wantedStems.Count + wantedPaks.Count} "
                          + $"already at {width}px{(force ? "   (--force: decoding again anyway)" : "")}");
        Console.WriteLine();

        var started = DateTime.Now;
        var batch = await ArtDecode.EnsureCachedAsync(new ArtRequest(
            proton, install, width, wantedStems, wantedPaks, Force: force)).ConfigureAwait(false);
        var elapsed = DateTime.Now - started;

        if (batch.Note.Length > 0)
        {
            Console.WriteLine($"  note         {batch.Note}");
            Console.WriteLine();
        }

        Console.WriteLine($"  ran          {batch.Ran}   exit {batch.ExitCode}   "
                          + $"{elapsed.TotalSeconds:0.0}s   {batch.Decoded}/{batch.Outcomes.Count} decoded");
        if (!batch.Ran)
        {
            Console.WriteLine("               the host was not started: every item was already "
                              + "decoded at this width");
        }
        Console.WriteLine();

        var unreadable = 0;
        foreach (var outcome in batch.Outcomes)
        {
            if (!outcome.Ok)
            {
                Console.WriteLine($"  err  {outcome.Kind,-4} {outcome.Input}");
                Console.WriteLine($"         {outcome.Reason}");
                continue;
            }

            if (!Verify(outcome, out var detail))
                unreadable++;

            Console.WriteLine($"  ok   {outcome.Kind,-4} {outcome.Input}");
            Console.WriteLine($"         source     {outcome.Source}");
            Console.WriteLine($"         cache      {outcome.CachePath}");
            Console.WriteLine($"         {detail}");
        }

        Console.WriteLine();
        Console.WriteLine($"  records      {batch.RecordsPath}");

        // The records file is the host's word for what it did; printing it is the
        // difference between "the launcher read the cache" and "the decoder said so".
        foreach (var line in ReadRecords(batch.RecordsPath))
            Console.WriteLine($"    | {line}");

        if (!keep)
            TryDelete(batch.RecordsPath);

        Console.WriteLine();
        var failures = batch.Outcomes.Count - batch.Decoded;
        Console.WriteLine($"  {batch.Decoded} decoded, {failures} not, {unreadable} undecodable after decoding");

        if (batch.Decoded == 0)
        {
            Console.WriteLine();
            Console.WriteLine(batch.Ran
                ? "EXIT 3  nothing decoded — the lines above are the host's own reasons."
                : "EXIT 3  nothing is cached and the host did not run.");
            return 3;
        }

        // A decoded file the launcher cannot read back is worse than a map with no
        // art, so it is its own code: the cache would look populated and the pane
        // would stay blank.
        if (unreadable > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"EXIT 4  {unreadable} file(s) decoded but would not read back — "
                              + "the cache is populated with pictures the launcher cannot use.");
            return 4;
        }

        // A partial result is still a pass: an install legitimately ships maps with
        // no loadscreen of their own, and the count above says how many. Nothing is
        // hidden by this — every failure printed its reason.
        Console.WriteLine();
        Console.WriteLine($"EXIT 0  {batch.Decoded} of {batch.Outcomes.Count} pictures are in the cache "
                          + "and read back at the size they claim.");
        return 0;
    }

    /// <summary>
    /// The cache file read back the way the launcher reads it, then the PNG built
    /// the way the pane builds it. Both halves matter: a file that has the right
    /// header and a payload the renderer cannot use would otherwise pass.
    /// </summary>
    static bool Verify(ArtOutcome outcome, out string detail)
    {
        if (!ArtCache.TryRead(outcome.CachePath, out var pixels, out var error) || pixels is null)
        {
            detail = $"UNREADABLE by the launcher: {error}";
            return false;
        }

        var png = LoadscreenArt.ToPng(pixels);
        var bytes = new FileInfo(outcome.CachePath).Length;

        if (png is null)
        {
            detail = $"{pixels.Width}x{pixels.Height} cached, but the pixels would not encode to a bitmap";
            return false;
        }

        detail = $"{pixels.Width}x{pixels.Height} (source {pixels.SourceWidth}px), "
                 + $"cache {bytes / 1024} KB, png {png.Length / 1024} KB";
        return true;
    }

    /// <summary>A cache entry the launcher would accept, by the format's own rule:
    /// the file exists, its header is this version, and it is at most this width.</summary>
    static bool IsCached(string path)
        => ArtCache.TryReadHeader(path, out var header, out _) && header.Width > 0;

    /// <summary>Every <c>&lt;map&gt;_loadscreen.rpak</c> in the install, as stems.
    /// The same directory rule <see cref="LoadscreenResolver.ListArtPaks"/> uses,
    /// and the suffix is what separates the two sets: <c>loadscreen_*.rpak</c> are
    /// the standalone art sets and never end in <c>_loadscreen.rpak</c> unless
    /// something shipped a map actually called "loadscreen".</summary>
    static List<string> MapStems(string install)
    {
        var stems = new List<string>();
        try
        {
            var dir = Path.Combine(install, "paks", "Win64");
            if (!Directory.Exists(dir))
                return stems;

            foreach (var pak in Directory.EnumerateFiles(dir, "*_loadscreen.rpak"))
            {
                var stem = Path.GetFileName(pak)[..^"_loadscreen.rpak".Length];
                if (stem.Length > 0)
                    stems.Add(stem);
            }
            stems.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // An install we cannot enumerate is one with no art, not a crash.
        }
        return stems;
    }

    static string DescribeCompatData()
    {
        var compatdata = ArtDecode.CompatDataDir();
        var prefix = PrefixLayout.WinePrefixOf(compatdata);
        if (!Directory.Exists(prefix))
            return $"{compatdata}  (no prefix yet — the first decode creates it)";

        var built = PrefixLayout.IsProtonPrefix(prefix) ? "built by Proton" : "not built by Proton";
        return $"{compatdata}  ({built})";
    }

    static IEnumerable<string> ReadRecords(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).Where(l => l.Length > 0) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    static IEnumerable<string> Split(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return string.Empty;
    }
}
