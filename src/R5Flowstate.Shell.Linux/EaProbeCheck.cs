using System;
using System.IO;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the EA side of things: which Proton build is here, what the
/// prefix holds, and whether anything is listening on the ports the game dials to
/// reach the EA App.
///
///     R5Flowstate --ea-probe
///
/// Nothing is created, nothing is started, nothing is written — not a prefix, not
/// a settings key, not a log. It does look for the Wine inside the selected Proton
/// build and for winetricks on PATH, which is how those two steps are verified to
/// be live rather than assumed.
///
/// Two verdicts are printed, and one of them says <c>unknown</c> on purpose:
/// whether the game can satisfy its own install check cannot be measured until an
/// EA App exists in the prefix, and guessing at it would be exactly the kind of
/// claim this launcher has been careful not to make.
/// </summary>
static class EaProbeCheck
{
    public static int Run()
    {
        Loc.Initialize(null);

        var settings = LinuxSettings.Load();

        var gamePrefix = string.IsNullOrWhiteSpace(settings.PrefixPath)
            ? LinuxSettings.DefaultPrefixPath
            : settings.PrefixPath.Trim();

        var protonDir = ProtonDiscovery.PickLatest(ProtonDiscovery.Discover())?.Dir
                        ?? settings.ProtonDir.Trim();

        Console.WriteLine("EA probe (read-only). There is one runtime now: Proton, and the EA App "
                          + "lives in the game's prefix.");
        Console.WriteLine();
        Console.WriteLine($"  proton       {(string.IsNullOrWhiteSpace(protonDir) ? "no Proton build found" : protonDir)}");
        var wine = ProtonWine.ProtonWineBinary(protonDir);
        var tools = ProtonWine.ProtonWineToolchain.ForProton(protonDir);
        Console.WriteLine($"  proton wine  {wine ?? "not where Proton keeps it"}");
        Console.WriteLine($"  winetricks   {ProtonWine.Winetricks() ?? "not on PATH"}");
        if (tools is not null)
        {
            Console.WriteLine($"               usable for a prefix prep: {tools.Describe()}");
        }
        Console.WriteLine($"  prefix       {DescribePrefix(gamePrefix)}");
        Console.WriteLine();

        Console.WriteLine("  EA App");
        var installed = ProtonLauncher.FindEaDesktopExe(gamePrefix);
        Console.WriteLine($"    desktop exe       {installed ?? "not installed"}");
        var found = EaPrefixPrep.Probe(gamePrefix);
        Console.WriteLine($"    components        {found.Describe()}");
        Console.WriteLine($"    missing           {(EaPrefixPrep.Missing(found) is { Count: > 0 } m
            ? string.Join(", ", m) : "nothing")}");

        var log = ProtonLauncher.FindNewestEaLog(gamePrefix);
        Console.WriteLine($"    newest EA log     {log ?? "none"}");
        if (log is not null)
        {
            foreach (var line in ProtonLauncher.Tail(log, 5))
                Console.WriteLine($"      | {line}");
        }

        Console.WriteLine();
        Console.WriteLine("  channel  (the host's loopback — this is what EA and the game meet on)");
        var probes = EaChannelProbe.Probe();
        foreach (var probe in probes)
            Console.WriteLine($"    {probe.Describe(),-22} {probe.Detail}");
        var (verdictLine, listening) = EaChannelProbe.Verdict(probes);
        Console.WriteLine();
        Console.WriteLine($"    verdict   {verdictLine}");

        Console.WriteLine();
        Console.WriteLine("  verdicts");
        Console.WriteLine($"    socket         {(listening ? "listening" : "no listener")}" +
                          "  — necessary, not sufficient");
        Console.WriteLine("                   the LSX handshake above this socket is an AES " +
                          "challenge-response the");
        Console.WriteLine("                   launcher holds no key for; only the game's own log " +
                          "lines say whether");
        Console.WriteLine("                   identity arrived. Corroborate with the EA log above.");
        var (processList, installCheck) = DiscoveryVerdicts(installed);
        PrintVerdict("process list", processList);
        PrintVerdict("install check", installCheck);

        Console.WriteLine();
        Console.WriteLine("Nothing was created, started or written.");

        if (installed is not null && !listening)
        {
            Console.WriteLine();
            Console.WriteLine($"EXIT 2  EA is installed ({installed}) but nothing is listening on " +
                              $"{EaChannelProbe.LsxPort} — start the EA App and sign in, then probe again.");
            return 2;
        }
        return 0;
    }

    /// <summary>One verdict sentence, wrapped under its own label. The wrap is by
    /// width rather than hand-placed, because these sentences change and a
    /// hand-wrapped one drifts out of its column the moment it does.</summary>
    static void PrintVerdict(string label, string text)
    {
        const int width = 66;
        var indent = new string(' ', 19);
        var words = text.Split(' ');
        var line = new System.Text.StringBuilder();
        Console.Write($"    {label,-14} ");

        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                Console.WriteLine(line.ToString());
                Console.Write(indent);
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        Console.WriteLine(line.ToString());
    }

    /// <summary>
    /// The two things a probe cannot measure, as sentences. They used to depend on the
    /// runtime, which is why the pair survived the split's removal: the process-list
    /// answer was the split's whole risk (a wineserver is keyed to a prefix, so a game
    /// in the Proton prefix could not see an EA App in a Wine one), and with one prefix
    /// that barrier is gone by construction rather than by bridge — but "gone by
    /// construction" is still a claim worth printing rather than assuming, and the
    /// install-check answer still has two arms, because the prefix may or may not have
    /// an EA Desktop in it.
    ///
    /// The install-check verdict went stale the day the EA App actually installed
    /// (2026-09-22): it used to say "cannot be measured until an EA App exists in a
    /// prefix" <em>in a prefix that has one</em>.
    /// </summary>
    internal static (string ProcessList, string InstallCheck) DiscoveryVerdicts(string? eaDesktopExe)
    {
        var processList =
            "same prefix — EA Desktop and the game share this prefix's wineserver, so "
            + "EADesktop.exe is in the game's own process list. Whether the game's check "
            + "is satisfied by that is its own log's question.";

        var installCheck = eaDesktopExe is null
            ? "unknown — the game's install check reads something in the prefix, and no EA "
              + "App is installed here yet, so there is nothing for it to find."
            : "EA Desktop exists here, so there is something for the check to find; whether "
              + "it does is the client's log's answer — `[OFFLINE-GUARD] patch D … install "
              + "check still fatal` means it does not.";

        return (processList, installCheck);
    }

    /// <summary>What a prefix is, in the terms the launcher reasons in — and, when
    /// it is not there at all, that it is not there.</summary>
    static string DescribePrefix(string prefixPath)
    {
        var root = PrefixLayout.WinePrefixOf(prefixPath);
        if (!Directory.Exists(root))
            return $"{prefixPath} — does not exist yet";

        var parts = new System.Collections.Generic.List<string> { "proton prefix" };
        var arch = PrefixLayout.DeclaredArch(root);
        if (arch is not null)
            parts.Add(arch);
        parts.Add(PrefixLayout.IsBootstrapped(root) ? "bootstrapped" : "not bootstrapped");
        parts.Add(PrefixLayout.IsProtonPrefix(root) ? "built by Proton" : "not built by Proton");
        var driveC = PrefixLayout.DriveCFor(prefixPath);
        parts.Add(Directory.Exists(driveC) ? $"drive_c {driveC}" : "no drive_c");
        return $"{prefixPath} — {string.Join(", ", parts)}";
    }
}
