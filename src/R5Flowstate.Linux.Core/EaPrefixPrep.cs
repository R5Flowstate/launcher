namespace R5Flowstate.Linux.Core;

/// <summary>
/// What the launcher can see of the pieces the EA App expects in a prefix. Read
/// from the files the verbs leave behind rather than by asking Wine, which would
/// mean starting one — and this answer is needed before deciding whether to start
/// anything at all.
/// </summary>
public sealed record EaPrefixComponents(bool CoreFonts, bool VcRuntime, bool Windows10)
{
    public bool Complete => CoreFonts && VcRuntime && Windows10;

    public string Describe()
        => $"fonts {(CoreFonts ? "in" : "missing")}, VC++ runtime {(VcRuntime ? "in" : "missing")}, "
           + $"Windows 10 {(Windows10 ? "set" : "not set")}";
}

/// <summary>The whole prep attempt: the lines to show, one summary, and whether the
/// prefix came out with everything the EA App expects.</summary>
public sealed record EaPrepOutcome(bool Ok, IReadOnlyList<string> Lines, string Summary);

/// <summary>
/// The redistributables the EA App expects and Wine does not ship: the Microsoft
/// core fonts, the VC++ runtimes, and a Windows 10 version stamp. Done through
/// winetricks, in the one prefix the EA App reads — the game's.
///
/// <para><b>Whose Wine writes it.</b> winetricks is a shell script: it runs whatever
/// <c>$WINE</c> names, so the prefix is prepared by the Wine inside the selected
/// Proton build (<see cref="ProtonWine.ProtonWineToolchain.ForProton"/>). The
/// registry these verbs write is the one the runtime that owns the prefix reads;
/// driving a Proton prefix with any other Wine is the unsupported shape this
/// avoids.</para>
///
/// <para><b>How "already there" is decided.</b> By the markers winetricks' own verbs
/// leave, and specifically by their being <em>real files</em>. This is the part that
/// has to be right, on evidence rather than principle: Proton's default prefix ships
/// Arial, Times, Courier and the VC++ builtins as symlinks into its own
/// <c>files/share</c>, so a present file proves nothing about who put it there — but
/// a symlink is Wine's builtin and a real file is somebody's install. Wine ships no
/// <c>mfc140.dll</c> builtin at all (checked against this box's wine 10 and Proton's
/// Wine 11), and <c>mfc140.dll</c> is exactly what winetricks records vcrun2019
/// with, so it is a clean signal either way. The version stamp is read out of
/// <c>system.reg</c>, which is plain text: no Wine API, and no Wine run.</para>
///
/// <para><b>Refusals rather than improvisation.</b> No prefix yet, a prefix Proton
/// did not create, a prefix something is running in, no winetricks, no Wine inside
/// the Proton build — each is reported with its own words and none of them is worked
/// around. The one that matters is "in use": the version stamp and the DLL overrides
/// go into a registry that a live wineserver holds in memory and writes back, so
/// writing under it is the thing that would silently lose the change.</para>
/// </summary>
public static class EaPrefixPrep
{
    public const string CoreFontsVerb = "corefonts";
    public const string VcRunVerb = "vcrun2019";
    public const string Win10Verb = "win10";

    /// <summary>The verbs, in the order they run: the two that install something,
    /// then the setting.</summary>
    public static IReadOnlyList<string> Verbs { get; } = new[] { CoreFontsVerb, VcRunVerb, Win10Verb };

    /// <summary>What winetricks touches at the end of its corefonts verb.</summary>
    const string CoreFontsMarker = "corefonts.installed";

    /// <summary>What winetricks records vcrun2019 with: the native MFC runtime, which
    /// Wine has no builtin of.</summary>
    const string VcRunMarker = "mfc140.dll";

    /// <summary>What is already in the prefix. A missing or unreadable prefix reads as
    /// nothing being there, which is the safe direction: the refusal in
    /// <see cref="Refusal"/> is what stops that from becoming a bootstrap.</summary>
    public static EaPrefixComponents Probe(string prefixPath)
    {
        var driveC = PrefixLayout.DriveCFor(prefixPath);
        return new EaPrefixComponents(
            NativeFile(Path.Combine(driveC, "windows", "Fonts", CoreFontsMarker)),
            NativeFile(Path.Combine(driveC, "windows", "system32", VcRunMarker)),
            WindowsVersionIsTen(PrefixLayout.WinePrefixOf(prefixPath)));
    }

    /// <summary>The verbs still to run, given what the probe found.</summary>
    public static IReadOnlyList<string> Missing(EaPrefixComponents found)
    {
        var missing = new List<string>();
        if (!found.CoreFonts) missing.Add(CoreFontsVerb);
        if (!found.VcRuntime) missing.Add(VcRunVerb);
        if (!found.Windows10) missing.Add(Win10Verb);
        return missing;
    }

    /// <summary>
    /// Why the prep cannot run at all, or null when it can — one place, so the button
    /// and the install press refuse for the same reasons in the same words.
    /// </summary>
    public static string? Refusal(string prefixPath, string protonDir)
    {
        if (string.IsNullOrWhiteSpace(prefixPath))
            return "there is no prefix to prepare.";

        var winePrefix = PrefixLayout.WinePrefixOf(prefixPath);
        if (!PrefixLayout.IsBootstrapped(winePrefix))
        {
            return $"the game's prefix does not exist yet ({winePrefix}). Proton creates it on the "
                   + "installer's first run, and the runtimes can only be added to a prefix that is "
                   + "there — press Install EA App (or Prepare EA Prefix) again once it has.";
        }

        // A prefix only Wine bootstrapped is not the prefix Proton would have made,
        // and filling it in is Proton's job (setup_prefix), not this launcher's.
        if (!PrefixLayout.IsProtonPrefix(winePrefix))
        {
            return $"{winePrefix} has no creation_sync_guard, so Proton did not create it. Nothing "
                   + "here writes into a prefix someone else built.";
        }

        if (ProtonWine.IsPrefixInUse(prefixPath))
        {
            return "something is running in this prefix — its Wine server has it open, and a "
                   + "version stamp or a DLL override written under a live Wine server is a change "
                   + "the server overwrites when it exits. Quit the EA App and the game, then press "
                   + "again.";
        }

        if (ProtonWine.Winetricks() is null)
            return "winetricks is not installed — install it (e.g. `pacman -S winetricks`) and "
                   + "press again.";

        if (ProtonWine.ProtonWineToolchain.ForProton(protonDir) is null)
        {
            return "no Wine inside the Proton build "
                   + $"({(string.IsNullOrWhiteSpace(protonDir) ? "no Proton build selected" : protonDir)}), "
                   + "so there is nothing to run the verbs with.";
        }

        return null;
    }

    /// <summary>
    /// Run whatever is missing. Never throws, never improvises: every refusal comes
    /// back as a summary the caller logs, and a verb that fails is reported with
    /// winetricks' own last lines rather than retried or hidden.
    /// </summary>
    public static async Task<EaPrepOutcome> RunAsync(string prefixPath,
        string protonDir, Action<string>? log = null, CancellationToken ct = default)
    {
        var lines = new List<string>();
        void Note(string line)
        {
            lines.Add(line);
            log?.Invoke(line);
        }

        if (Refusal(prefixPath, protonDir) is { } why)
            return new EaPrepOutcome(false, lines, why);

        var winePrefix = PrefixLayout.WinePrefixOf(prefixPath);
        var tools = ProtonWine.ProtonWineToolchain.ForProton(protonDir)!;

        var before = Probe(prefixPath);
        var verbs = Missing(before);
        if (verbs.Count == 0)
        {
            Note($"{winePrefix} already has everything the EA App expects ({before.Describe()}).");
            return new EaPrepOutcome(true, lines, "nothing to do — " + before.Describe() + ".");
        }

        Note($"{winePrefix}: {before.Describe()}. Installing {string.Join(", ", verbs)} "
             + $"with {tools.Describe()}.");

        var ok = true;

        // The two that install something first, the version stamp last: it is a
        // setting, and setting it after an installer has run is what the button has
        // always done. A failure in one is reported and the run carries on — a missing
        // font set is not a reason to leave the rest half-done.
        var fileVerbs = verbs.Where(v => v != Win10Verb).ToList();
        if (fileVerbs.Count > 0)
        {
            var run = await ProtonWine.WinetricksAsync(winePrefix, fileVerbs, tools,
                ct: ct).ConfigureAwait(false);
            foreach (var line in run.Tail(12)) Note("winetricks: " + line);
            ok &= run.Ok;
            if (!run.Ok)
            {
                Note($"winetricks did not finish cleanly (exit {run.ExitCode}"
                     + (run.TimedOut ? ", timed out" : "") + ").");
            }
        }

        if (verbs.Contains(Win10Verb))
        {
            Note("Setting the prefix's Windows version to 10 — the EA App refuses anything older.");
            var win10 = await ProtonWine.WinetricksAsync(winePrefix, new[] { Win10Verb }, tools,
                ct: ct).ConfigureAwait(false);
            foreach (var line in win10.Tail(6)) Note("winetricks: " + line);
            ok &= win10.Ok;
        }

        var after = Probe(prefixPath);
        var stillMissing = Missing(after);
        var summary = stillMissing.Count == 0
            ? $"{winePrefix} is prepared: {after.Describe()}."
            : $"{winePrefix} is not fully prepared: {after.Describe()}"
              + $" — still missing {string.Join(", ", stillMissing)}"
              + (ok ? "." : ", and winetricks reported a failure above.");
        Note(summary);
        return new EaPrepOutcome(ok && stillMissing.Count == 0, lines, summary);
    }

    /// <summary>A file that is really there, as opposed to a Wine builtin symlinked
    /// into the prefix by Proton or by wineboot. The distinction is the whole probe:
    /// see the class comment.</summary>
    static bool NativeFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the prefix's registry says Windows 10, read as text from
    /// <c>system.reg</c>. Both writers of that answer are covered by the one key:
    /// winetricks' win10 verb sets <c>ProductName</c> to "Windows 10 Pro", and so
    /// does Wine and Proton's own default. It is the honest signal either way — a
    /// prefix somebody set to Windows 7 reports Windows 7, which is the case worth
    /// catching, because the EA App refuses to run on it.
    /// </summary>
    static bool WindowsVersionIsTen(string winePrefix)
    {
        const string section = "[Software\\\\Microsoft\\\\Windows NT\\\\CurrentVersion]";
        try
        {
            var path = PrefixLayout.SystemRegPath(winePrefix);
            if (!File.Exists(path))
                return false;

            var inside = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    // Sections are visited in file order, so leaving the one we want
                    // ends the search; the section has no values after ProductName that
                    // this needs.
                    if (inside)
                        return false;
                    inside = line.StartsWith(section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inside)
                    continue;
                if (line.StartsWith("\"ProductName\"=", StringComparison.OrdinalIgnoreCase))
                    return line.Contains("Windows 10", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
