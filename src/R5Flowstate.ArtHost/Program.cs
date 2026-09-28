using System.Text;
using R5Flowstate.Content.Rpak;

namespace R5Flowstate.ArtHost;

/// <summary>
/// Decode loadscreens for the Linux launcher, from inside the prefix.
///
/// <para>The launcher cannot do this itself: the paks are Oodle-encoded and
/// <c>oo2core_8_win64.dll</c> is a PE DLL that a native Linux process cannot
/// load. This host is the Windows process where it can, so the launcher spawns
/// it once per batch — one run for a mode's map thumbnails, one for the hero —
/// and reads the pixels back out of its cache directory afterwards.</para>
///
/// <para>Nothing here decides what art the launcher should show: it is handed
/// stems and pak paths and answers with pixels or with the decoder's own reason.
/// The fallback chain (a map's own pak, then a shipped art loadscreen naming the
/// same location, then the parent map) lives in
/// <see cref="LoadscreenResolver.TryDecode"/> and is used unchanged — it is the
/// same code the Windows shell decodes through, so the Linux launcher cannot
/// drift from it.</para>
///
/// <para><b>Output contract</b> (tab-separated, one record per line):
/// <code>
/// host  &lt;version&gt;
/// ok    stem  &lt;input&gt;  &lt;cache file name&gt;  &lt;w&gt;  &lt;h&gt;  &lt;source width&gt;  &lt;source path&gt;
/// err   stem  &lt;input&gt;  &lt;reason&gt;
/// done  &lt;ok count&gt;  &lt;fail count&gt;
/// </code>
/// Every record goes to stdout <em>and</em> to <c>--records &lt;file&gt;</c>, and the
/// file is the one that matters: <b>a Proton-launched helper's stdout never
/// reaches its parent.</b> Measured here, twice: `proton run cmd.exe /c echo hi`
/// and this host, each with .NET's <c>RedirectStandardOutput</c>, both returning
/// zero lines while their exit codes propagated normally. Wine gives a console
/// process a console and the standard handles follow it, so the parent's pipe
/// stays empty. Anything that has to be reported therefore has to be a file the
/// host writes — which is the same shape as the pixels themselves.</para>
/// </summary>
internal static class Program
{
    const int ExitOk = 0;
    const int ExitUsage = 2;
    const int ExitNothingDecoded = 3;

    static int Main(string[] argv)
    {
        var install = string.Empty;
        var outDir = string.Empty;
        var recordsPath = string.Empty;
        var width = LoadscreenResolver.DisplayMaxWidth;
        var stems = new List<string>();
        var paks = new List<string>();

        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            switch (arg)
            {
                case "--install":
                    if (++i >= argv.Length) return Usage("--install needs a folder");
                    install = argv[i];
                    break;
                case "--out":
                    if (++i >= argv.Length) return Usage("--out needs a folder");
                    outDir = argv[i];
                    break;
                case "--records":
                    if (++i >= argv.Length) return Usage("--records needs a file");
                    recordsPath = argv[i];
                    break;
                case "--width":
                    if (++i >= argv.Length || !int.TryParse(argv[i], out width))
                        return Usage("--width needs a number");
                    break;
                case "--stem":
                    if (++i >= argv.Length) return Usage("--stem needs a map stem");
                    stems.Add(argv[i]);
                    break;
                case "--pak":
                    if (++i >= argv.Length) return Usage("--pak needs a file");
                    paks.Add(argv[i]);
                    break;
                case "--help":
                case "-h":
                    try { Console.WriteLine(Help); } catch { /* no console handle */ }
                    return ExitOk;
                default:
                    return Usage($"unknown argument: {arg}");
            }
        }

        if (outDir.Length == 0)
            return Usage("--out is required: the host writes pixels, it does not print them");
        if (stems.Count == 0 && paks.Count == 0)
            return Usage("nothing asked for: pass --stem and/or --pak");

        using var records = RecordSink.Open(recordsPath);
        Record(records, $"host\t{ThisVersion()}");
        if (!LoadscreenResolver.OodleAvailable)
        {
            // Said once, up front, because this is the difference between "that
            // map has no art" and "this install's art cannot be read at all" —
            // and every request below is about to fail for the same reason.
            Record(records, "warn\toolde\t"
                + "oo2core_8_win64.dll is not beside the art host; only uncompressed loadscreens can be read");
        }

        // The decoder is installed once for the whole batch: it is the same
        // lookup for every item, and a batch is the unit the launcher spawns.
        if (install.Length > 0)
            LoadscreenResolver.PrepareInstall(install);

        var ok = 0;
        var failed = 0;

        foreach (var stem in stems)
        {
            if (Decode(records, install, stem, LoadscreenResolver.TryDecode, "stem", outDir, width))
                ok++;
            else
                failed++;
        }

        foreach (var pak in paks)
        {
            if (Decode(records, install, pak, DecodePak, "pak", outDir, width))
                ok++;
            else
                failed++;
        }

        Record(records, $"done\t{ok}\t{failed}");
        return ok == 0 ? ExitNothingDecoded : ExitOk;

        // A named pak is decoded straight; the install only matters to the
        // stem path, which has to look the pak up first.
        static bool DecodePak(string _, string pak, out LoadscreenPixels? pixels,
            out string? error, int maxWidth)
            => LoadscreenResolver.TryDecodePak(pak, out pixels, out error, maxWidth);
    }

    /// <summary>One item: decode, write the cache file, report. The key rule is
    /// <see cref="ArtCache"/>'s, shared with the launcher that reads the file, so
    /// a cache hit on the Linux side and a write here cannot disagree.</summary>
    static bool Decode(RecordSink? records, string install, string input,
        DecodeFn decode, string kind, string outDir, int width)
    {
        string? error;
        LoadscreenPixels? pixels;

        try
        {
            if (!decode(install, input, out pixels, out error, width) || pixels is null)
            {
                Record(records, $"err\t{kind}\t{input}\t{error ?? "decode failed"}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Record(records, $"err\t{kind}\t{input}\t{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        var key = kind == "pak" ? ArtCache.KeyForPak(input) : ArtCache.KeyForStem(input);
        var file = ArtCache.FileNameFor(key);
        var path = Path.Combine(outDir, file);

        if (!ArtCache.Write(path, pixels, out var writeError))
        {
            Record(records, $"err\t{kind}\t{input}\tart cache write failed: {writeError}");
            return false;
        }

        Record(records, $"ok\t{kind}\t{input}\t{file}\t{pixels.Width}\t{pixels.Height}"
            + $"\t{pixels.SourceWidth}\t{pixels.SourcePath}");
        return true;
    }

    /// <summary>
    /// One record, to both channels. The file is for the launcher, which never
    /// reads this process's stdout (<c>proton run</c> discards a child's streams);
    /// stdout is for a human running the host by hand.
    ///
    /// <para>The order matters, and so does the guard. This exe is a GUI-subsystem
    /// binary now, so most of the time it has no console handle at all — and a
    /// stdout write that throws must not be able to take the record with it. That
    /// is not hypothetical: a helper that died before writing its first record is
    /// exactly what cost a whole debugging cycle once, when every map read as "the
    /// art host did not report this one" and the real fault was three files away.
    /// A line in the file is a fact; a line in a stream nobody reads is a courtesy.</para>
    /// </summary>
    static void Record(RecordSink? records, string line)
    {
        records?.Write(line);
        try { Console.WriteLine(line); }
        catch { /* no console handle, which is the normal case here */ }
    }

    /// <summary>
    /// The records file, flushed per line.
    ///
    /// <para>Unbuffered on purpose: the launcher may kill the host on timeout and
    /// still wants to know how far it got, and a decode that dies mid-batch
    /// (Oodle on a corrupt pak, an out-of-memory block table) leaves no exit code
    /// worth reading. A line that reached the file is a fact; a line sitting in a
    /// buffer is not.</para>
    ///
    /// <para>Failure to open the file is not fatal — the host still decodes and
    /// still writes pixels — so this degrades to stdout-only rather than refusing
    /// the batch.</para>
    /// </summary>
    sealed class RecordSink : IDisposable
    {
        readonly StreamWriter? _writer;

        RecordSink(StreamWriter? writer) => _writer = writer;

        public static RecordSink? Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Truncate: one run, one file. A stale record from a previous
                // batch must never be read as this one's.
                var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                var w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                return new RecordSink(w);
            }
            catch
            {
                return null;
            }
        }

        public void Write(string line)
        {
            try
            {
                // One line per record or the reader's split-on-newline loses
                // count. A decoder reason can carry an exception message with a
                // newline in it; the tabs are the delimiters and stay.
                _writer?.WriteLine(line.Replace('\r', ' ').Replace('\n', ' '));
            }
            catch
            {
                // A full disk mid-batch: the pixels are the product, not this.
            }
        }

        public void Dispose()
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Nothing left to report it to.
            }
        }
    }

    delegate bool DecodeFn(string install, string input, out LoadscreenPixels? pixels,
        out string? error, int maxWidth);

    static int Usage(string why)
    {
        // Guarded for the same reason Record's stdout is: this is a GUI-subsystem
        // binary, so a usage error may well have nowhere to be printed. The exit
        // code is what a script reads, and it is returned either way.
        try
        {
            Console.Error.WriteLine($"r5f-arthost: {why}");
            Console.Error.WriteLine(Help);
        }
        catch { /* no console handle */ }

        return ExitUsage;
    }

    static string ThisVersion()
        => typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    const string Help =
        """
        r5f-arthost — decode R5Flowstate loadscreen paks into the launcher's art cache.

          --install <folder>   the game install (holds paks/Win64)
          --out <folder>       where decoded pictures are written
          --records <file>     also write the records here (the launcher reads this)
          --width <px>         decoded width cap (default 960)
          --stem <map stem>    decode a map's loadscreen (repeatable)
          --pak <file.rpak>    decode one named loadscreen pak (repeatable)

        Records are tab-separated, one per line: host | ok | err | warn | done.
        They go to stdout and to --records; under Proton only the file survives.
        """;
}
