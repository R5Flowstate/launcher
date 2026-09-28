using System.Diagnostics;

namespace R5Flowstate.Linux.Core;

/// <summary>The outcome of a one-shot helper run (winetricks, a Wine builtin).
/// Exit code plus the lines it printed, because those lines are usually the only
/// account of why it failed.</summary>
public sealed record HelperRunResult(int ExitCode, bool TimedOut, IReadOnlyList<string> Lines)
{
    public bool Ok => !TimedOut && ExitCode == 0;

    public string Tail(int count = 8)
        => string.Join("\n", Lines.Skip(Math.Max(0, Lines.Count - count)));
}

/// <summary>
/// Reaches the Wine that lives <em>inside a Proton build</em>, to do the things
/// that have to be done to a prefix from outside the game: install Microsoft
/// redistributables with winetricks, run a Wine builtin, and answer "is this
/// prefix in use".
///
/// <para><b>This is not a Wine runtime.</b> There is no system Wine in this
/// launcher any more and no second prefix: the EA App and the game share the
/// game's Proton prefix, so the only Wine that may touch it is the one Proton
/// itself ships (<c>&lt;protonDir&gt;/files/bin/wine</c>). Every entry point here
/// takes that binary as an argument rather than discovering one, because there is
/// nothing to discover — and driving a Proton prefix with somebody else's Wine is
/// the unsupported shape this class exists to avoid.</para>
///
/// <para>Windows exes are launched through <see cref="ProtonLauncher"/>, which is
/// the other half of "run a Windows program here". The split is by role: a
/// <em>program the player asked for</em> goes through Proton; a
/// <em>maintenance step on the prefix</em> comes here.</para>
/// </summary>
public static class ProtonWine
{
    /// <summary>Mono and Gecko are what Wine wants to install on a prefix's first
    /// run, and they prompt. A prompt in a headless spawn is indistinguishable
    /// from a hang, so both are switched off for every helper: the EA App does not
    /// need either, and the alternative is a window nobody sees.</summary>
    public const string DefaultDllOverrides = "mscoree,mshtml=";

    // ---- winetricks ----

    /// <summary>How long a winetricks run gets. A first EA prep downloads about
    /// 100 MB from Microsoft and installs four packages, which on this box took
    /// most of five minutes with a cold cache — well past any sane "helper"
    /// timeout.</summary>
    public const int WinetricksTimeoutMs = 900_000;

    /// <summary>Run winetricks against a prefix. Only ever reached from a button or
    /// from an install press: it installs Microsoft redistributables, which is a
    /// download the player has to ask for.</summary>
    public static Task<HelperRunResult> WinetricksAsync(string winePrefixPath,
        IReadOnlyList<string> verbs, ProtonWineToolchain tools,
        int timeoutMs = WinetricksTimeoutMs, CancellationToken ct = default)
    {
        var winetricks = Winetricks();
        if (winetricks is null)
            return Task.FromResult(new HelperRunResult(1, false,
                new[] { "winetricks is not installed." }));

        return RunCaptured(BuildWinetricksStartInfo(winetricks, winePrefixPath, tools, verbs),
            timeoutMs, ct);
    }

    /// <summary>The argv for a winetricks run: <c>-q</c>, because a prompt in a spawn
    /// nobody can see is indistinguishable from a hang. Deliberately not <c>-f</c>
    /// (force): winetricks skipping a verb the prefix already has is the behaviour
    /// wanted here, and forcing is what would re-download a runtime that is already
    /// on disk.</summary>
    public static IReadOnlyList<string> WinetricksArgs(IReadOnlyList<string> verbs)
    {
        var args = new List<string> { "-q" };
        args.AddRange(verbs);
        return args;
    }

    /// <summary>
    /// The environment a winetricks run needs, as a start info that is built but not
    /// run — the suite asserts this shape without executing anything.
    ///
    /// <para><b>WINE and WINESERVER</b> are the whole trick: they point winetricks at
    /// the Wine of the runtime the prefix belongs to, and winetricks derives the same
    /// wineserver itself from <c>$WINE</c> ("${WINE}server"), which is why setting it
    /// is a statement of intent rather than a workaround.</para>
    ///
    /// <para><b>WINEARCH</b> is the launcher's standing win64 statement, and it is
    /// what makes winetricks bootstrap a prefix when there is none. There is none on
    /// this path either: the caller refuses a prefix that does not exist yet rather
    /// than letting a Wine built by hand stand in for Proton's first run.</para>
    ///
    /// <para><b>Deliberately no WINEDLLOVERRIDES.</b> The launcher's
    /// <c>mscoree,mshtml=</c> default exists to keep Mono/Gecko prompts out of a
    /// <em>launch</em>. winetricks sets the overrides each verb wants through the
    /// registry (<c>w_override_dlls</c>), and pre-seeding the variable is a lever its
    /// own verbs use.</para>
    /// </summary>
    public static ProcessStartInfo BuildWinetricksStartInfo(string winetricksBinary,
        string winePrefixPath, ProtonWineToolchain tools, IReadOnlyList<string> verbs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = winetricksBinary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in WinetricksArgs(verbs))
            psi.ArgumentList.Add(a);

        psi.Environment["WINEPREFIX"] = Path.GetFullPath(winePrefixPath);
        psi.Environment["WINE"] = tools.Wine;
        psi.Environment["WINESERVER"] = tools.WineServer;
        psi.Environment["WINEARCH"] = "win64";
        return psi;
    }

    /// <summary>winetricks is a shell script that reads WINEPREFIX, not a Wine
    /// program, so the launcher can only offer it when the box has one.</summary>
    public static string? Winetricks() => WhichOnPath("winetricks");

    /// <summary>Look a bare name up on PATH the way a shell would.</summary>
    public static string? WhichOnPath(string name)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full))
                    return full;
            }
        }
        catch { }
        return null;
    }

    // ---- running a builtin ----

    /// <summary>Run a Wine builtin (reg, regedit, …) inside a prefix through the
    /// Wine of the build that owns it. Used to read back what a prep wrote.</summary>
    public static Task<HelperRunResult> BuiltinAsync(string prefixPath, string wineBinary,
        IReadOnlyList<string> wineArgs, int timeoutMs = 60_000, CancellationToken ct = default)
        => HelperAsync(prefixPath, wineBinary, wineArgs, timeoutMs, ct);

    // ---- the Wine inside a Proton build ----

    /// <summary>The Wine inside a Proton build, which is the one the game itself
    /// runs on. Used to write into the game's prefix through the game's own Wine
    /// rather than any other — the registry a Proton prefix reads was written by
    /// this binary.</summary>
    public static string? ProtonWineBinary(string protonDir)
    {
        if (string.IsNullOrWhiteSpace(protonDir))
            return null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(protonDir, "files", "bin", "wine"),
                     Path.Combine(protonDir, "dist", "bin", "wine"),
                 })
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// The Wine a winetricks run should use, with the wineserver beside it.
    ///
    /// <para>The wineserver is the sibling of the wine binary on purpose, and
    /// deliberately not looked up on PATH: a PATH wineserver would be a different
    /// build, and a prefix belongs to the server that opened it. That is the mixing
    /// this whole class exists to prevent.</para>
    /// </summary>
    public sealed record ProtonWineToolchain(string Wine, string WineServer)
    {
        /// <summary>The Wine inside a Proton build. Null when there is no Proton, or
        /// when the build has no Wine where Proton keeps it.</summary>
        public static ProtonWineToolchain? ForProton(string protonDir)
        {
            var wine = ProtonWineBinary(protonDir);
            if (wine is null)
                return null;

            var server = Path.Combine(Path.GetDirectoryName(wine)!, "wineserver");
            return File.Exists(server) ? new ProtonWineToolchain(wine, server) : null;
        }

        public string Describe() => $"{Wine} (wineserver {WineServer})";
    }

    // ---- is the prefix in use ----

    /// <summary>
    /// True when a <em>Wine</em> process has <paramref name="prefixPath"/> open. The
    /// cheap, honest answer to "is this prefix in use", read from <c>/proc</c> rather
    /// than by asking Wine (which would mean starting one).
    ///
    /// <para>The caller that needs it is the EA prep: it refuses to prepare a prefix
    /// while the game's wineserver has it open, because that is the one operation in
    /// this launcher that could damage a 45 GB install.</para>
    ///
    /// <para><b>Which processes count.</b> Only processes whose executable is Wine's
    /// — <c>wine</c>, <c>wineserver</c>, <c>wine-preloader</c>, <c>proton</c>. A
    /// process that merely <em>names</em> the path does not hold the prefix open, and
    /// treating it as if it did would refuse the prep for anyone with a terminal, a
    /// pager or an editor pointed at their games directory — which is to say, for
    /// almost everyone who would press the button. Such a process is looked at twice:
    /// its arguments (a Wine start info spells the prefix out) and its environment
    /// (<c>WINEPREFIX</c>, which is how a wineserver knows its prefix at all).</para>
    ///
    /// Unreadable process entries are skipped — a sandbox that hides one is not
    /// evidence that nothing is running.
    /// </summary>
    public static bool IsPrefixInUse(string prefixPath)
    {
        if (string.IsNullOrWhiteSpace(prefixPath))
            return false;

        var needle = Path.GetFullPath(prefixPath);
        var self = Environment.ProcessId;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || !char.IsDigit(name[0]) || !int.TryParse(name, out var pid))
                    continue;
                if (pid == self)
                    continue;

                try
                {
                    var argv = SplitNul(File.ReadAllBytes(Path.Combine(dir, "cmdline")));
                    if (argv.Count == 0 || !IsWineExecutable(argv[0]))
                        continue;

                    if (argv.Any(a => a.Contains(needle, StringComparison.Ordinal)))
                        return true;

                    var env = SplitNul(File.ReadAllBytes(Path.Combine(dir, "environ")));
                    if (env.Any(e => e.StartsWith("WINEPREFIX=", StringComparison.Ordinal)
                                     && e["WINEPREFIX=".Length..].Contains(needle, StringComparison.Ordinal)))
                        return true;
                }
                catch
                {
                    // Gone, or not ours to read.
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>Wine's own binaries, by executable name. The Windows program inside
    /// the prefix is not on this list on purpose: what holds a prefix open is the
    /// wineserver and its loader, and a process called <c>r5apex.exe</c> is a client
    /// of one.</summary>
    static bool IsWineExecutable(string exe)
    {
        var name = Path.GetFileName(exe);
        if (name.Length == 0)
            return false;
        return name.StartsWith("wine", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("proton", StringComparison.OrdinalIgnoreCase);
    }

    static List<string> SplitNul(byte[] raw)
    {
        var parts = new List<string>();
        if (raw.Length == 0)
            return parts;
        var text = System.Text.Encoding.UTF8.GetString(raw);
        foreach (var part in text.Split('\0'))
        {
            if (part.Length > 0)
                parts.Add(part);
        }
        return parts;
    }

    // ---- the one shape for a helper ----

    /// <summary>One shape for every Wine-builtin helper call: run it in its prefix,
    /// capture what it said, never let a hung helper hang the launcher. winetricks
    /// does not come through here — it is a script rather than a Wine program, and it
    /// must not inherit the launching overrides — so it builds its own start info
    /// (<see cref="BuildWinetricksStartInfo"/>) and shares only
    /// <see cref="RunCaptured"/>.</summary>
    static Task<HelperRunResult> HelperAsync(string prefixPath, string binary,
        IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        psi.Environment["WINEPREFIX"] = Path.GetFullPath(prefixPath);
        psi.Environment["WINEDLLOVERRIDES"] = DefaultDllOverrides;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        return RunCaptured(psi, timeoutMs, ct);
    }

    /// <summary>Run a prepared helper and capture its output, whichever helper it is.
    /// The tail of this is the whole diagnosis when one fails: Wine writes to stderr
    /// and then runs headless, so the lines are usually the only account there is.</summary>
    static async Task<HelperRunResult> RunCaptured(ProcessStartInfo psi, int timeoutMs,
        CancellationToken ct)
    {
        var binary = psi.FileName;
        var lines = new List<string>();
        try
        {
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
            if (!p.Start())
                return new HelperRunResult(1, false, new[] { $"failed to start {binary}" });

            // winetricks asks questions when it is not given -q; a closed stdin makes
            // it take every default instead of blocking on a prompt nobody can see.
            try { p.StandardInput.Close(); } catch { }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var exit = p.WaitForExitAsync(cts.Token);
            var done = await Task.WhenAny(exit, Task.Delay(timeoutMs, cts.Token)).ConfigureAwait(false);
            if (!ReferenceEquals(done, exit))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                lock (lines) lines.Add($"{Path.GetFileName(binary)} did not finish within {timeoutMs / 1000}s");
                return new HelperRunResult(-1, true, new List<string>(lines));
            }

            await exit.ConfigureAwait(false);
            lock (lines)
                return new HelperRunResult(p.ExitCode, false, new List<string>(lines));
        }
        catch (OperationCanceledException)
        {
            return new HelperRunResult(-1, true, new List<string>(lines));
        }
        catch (Exception ex)
        {
            return new HelperRunResult(-1, false, new List<string>(lines) { ex.Message });
        }
    }
}
