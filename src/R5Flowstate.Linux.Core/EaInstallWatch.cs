namespace R5Flowstate.Linux.Core;

/// <summary>How an EA install attempt ends. There are more ways for it to end
/// than "it worked" and "it failed", and the difference matters to the player:
/// a bundle that never wrote a line did not fail the same way as one that wrote
/// forty and then stopped.</summary>
public enum EaInstallOutcome
{
    /// <summary>The installer is up and still working: leave it alone.</summary>
    Running,

    /// <summary>EA Desktop appeared in the prefix. The install worked.</summary>
    DesktopInstalled,

    /// <summary>It exited, it had written logs, and no EA Desktop appeared.</summary>
    LogStalled,

    /// <summary>It exited without writing anything: it died before it said a word.</summary>
    NoTrace,

    /// <summary>It is alive and slow enough that the launcher stops watching and
    /// hands over. Not a failure: the player finishes it in the EA window.</summary>
    StillWorking,
}

/// <summary>Which step of a WiX Burn bundle an attempt has reached, read from the
/// bundle's own log. Burn is silent on stdout for minutes at a time — a 234 MB
/// download writes nothing anywhere — so the phase is what turns that silence
/// into a sentence instead of letting it read as a hang.</summary>
public enum EaInstallPhase
{
    /// <summary>Nothing recognising has been written yet.</summary>
    Unknown,

    /// <summary>The bundle has started and registered itself in the prefix.</summary>
    Registering,

    /// <summary>It is fetching the MSI payload from EA's CDN.</summary>
    Downloading,

    /// <summary>The payload arrived and is being hash-checked.</summary>
    Verifying,

    /// <summary>It is applying the MSI — the step where Wine's MSI engine works.</summary>
    Installing,

    /// <summary>It has already reported a failure and is rolling back.</summary>
    Failed,
}

/// <summary>
/// The rules for reading an EA install attempt from the outside.
///
/// The launcher hands the installer to Proton and gets back a pid; the installer
/// is a WiX Burn bundle, so from there it is a black box that either produces an
/// EA Desktop or does not. Wine's grace window answers "did it start" and cannot
/// answer "did it do anything", which is how "I pressed Install and nothing
/// happened. nothing even appears" became unanswerable on this machine: Burn
/// started, wrote its own log into the prefix, and died without a window, and
/// the launcher had already reported that it was running.
///
/// So the second question is asked of what the attempt leaves behind — EA Desktop,
/// its log file, and the process itself. The rules are here, apart from the loop
/// that applies them, because they are the part worth testing without an installer
/// to run.
///
/// One correction is recorded here because it cost a real diagnosis (2026-09-22):
/// the first version of these rules had no idea whether the installer was still
/// running, and inferred its death from its log going quiet. A Burn bundle about
/// to download 234 MB then apply an MSI writes nothing to its log for five and a
/// half minutes, so the launcher reported "it exited without finishing" while
/// three of its processes were alive and working — and then stopped watching, so
/// the stderr of the real failure (an MSI apply that died in one second) was never
/// written down. Silence is a fact about a log. It is not a fact about a process.
///
/// The second correction, from the next attempt the same day, is the same mistake one
/// level down: an empty MSI log beside a 1603 was read here as "Wine's MSI engine
/// never opened the package". With the stderr finally kept, Wine said otherwise — it
/// opened the package, ran its actions, and halted at EA's own custom action
/// <c>JunoInitializeSession</c>, while the log stayed empty because Wine could not
/// open it at all (<c>MsiEnableLogW</c>, error 32: a sharing violation against the
/// file Burn was holding). Silence is a fact about a log, including this one.
///
/// The sentences the launcher prints about a failed attempt live here too, for the
/// same reason the rules do: a sentence is a claim, and the two that were wrong on
/// this machine — "it did not start" for a child that had failed and rolled back,
/// and "the engine never opened the package" from a log Wine could not write — were
/// wrong in ways a test over a fixture can catch.
/// </summary>
public static class EaInstallWatch
{
    /// <summary>How often the prefix is looked at. Two seconds is well below how
    /// fast Burn writes, so a live installer never looks quiet.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    /// <summary>When the launcher stops watching and hands the attempt to the
    /// player. Reaching this is not a failure: twenty minutes in, an installer that
    /// is still alive is simply a slow one, and the window is there to finish it in.
    /// It was ninety seconds until 2026-09-22, which a bundle cannot even finish
    /// its download inside.</summary>
    public static readonly TimeSpan StopAfter = TimeSpan.FromMinutes(20);

    /// <param name="desktopPresent">EA Desktop exists in the prefix now.</param>
    /// <param name="logExists">A log belonging to this attempt was found.</param>
    /// <param name="elapsed">Time since the installer was started.</param>
    /// <param name="installerAlive">The installer process is still running. While
    /// it is, no amount of quiet in its log means anything.</param>
    public static EaInstallOutcome Classify(bool desktopPresent, bool logExists,
        TimeSpan elapsed, bool installerAlive)
    {
        if (desktopPresent)
            return EaInstallOutcome.DesktopInstalled;

        // Alive is alive. A quiet log beside a live process is a download, a
        // decompression or a window waiting for a click — never a death.
        if (installerAlive)
            return elapsed >= StopAfter ? EaInstallOutcome.StillWorking : EaInstallOutcome.Running;

        // It is gone. Now what it left behind decides which kind of gone it is.
        return logExists ? EaInstallOutcome.LogStalled : EaInstallOutcome.NoTrace;
    }

    /// <summary>True for an outcome the watcher can stop on: every one but
    /// <see cref="EaInstallOutcome.Running"/>.</summary>
    public static bool IsFinal(EaInstallOutcome outcome) => outcome != EaInstallOutcome.Running;

    /// <summary>The sentence for a child that ended inside the launch's grace window.
    ///
    /// It used to read "exited immediately (code 1) — it did not start", which was
    /// the wrong fact twice over. How long it ran is the part that was missing: on
    /// 2026-09-22 05:01 the EA installer ran Burn's whole cache-and-apply cycle in
    /// nine seconds, failed at EA's own 1603 and rolled back, and the launcher told
    /// the player it had never started. Nine seconds with a log and a rollback in it
    /// is a different event from a process that dies before it speaks, and only the
    /// duration tells them apart — so the duration is what this says, and "it did
    /// not stay up" is all it claims about the outcome — no hint about *why*, because
    /// this box has already produced one counter-example to every hint that was tried
    /// (the nine-second child had got all the way to the MSI apply).</summary>
    public static string EarlyExitNote(string label, TimeSpan ran, int code) =>
        $"{label} exited after {ran.TotalSeconds:0.0}s (code {code}) — it did not stay up. "
        + "Everything it printed is the account of what happened.";

    /// <summary>The lines of a child's output worth putting in the log: the last
    /// <paramref name="max"/>, because a report is not a transcript. The classifier
    /// gets the whole capture instead — see <see cref="WindowsRun.CaptureLimit"/> for
    /// why those two numbers are not the same one.</summary>
    public static IReadOnlyList<string> QuotableLines(IReadOnlyList<string>? captured, int max = 40)
    {
        if (captured is null || captured.Count == 0 || max <= 0)
            return Array.Empty<string>();
        if (captured.Count <= max)
            return captured;

        var tail = new List<string>(max);
        for (var i = captured.Count - max; i < captured.Count; i++)
            tail.Add(captured[i]);
        return tail;
    }

    /// <summary>A file's size, or 0 for a path that is null, gone, or unreadable.
    /// The watcher compares sizes rather than reading the log on every poll; a
    /// file that cannot be measured is simply one that never grew.</summary>
    public static long BytesOf(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The end of a file, as text — for reading a phase out of a log that
    /// is still being written. Shared (Burn holds its log open) and never throwing:
    /// a log that cannot be read is one that says nothing, which is the truth.</summary>
    public static string TailText(string? path, int maxBytes = 4096)
    {
        try
        {
            if (path is null || maxBytes <= 0 || !File.Exists(path))
                return "";

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;
            var take = (int)Math.Min(length, maxBytes);
            fs.Seek(length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            var read = fs.Read(buffer, 0, take);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            // A read that started mid-file starts mid-line, and half a line is not
            // a line: drop it rather than mistaking a fragment for a marker.
            if (take < length)
            {
                var nl = text.IndexOf('\n');
                text = nl >= 0 ? text[(nl + 1)..] : "";
            }
            return text;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The phase a bundle has reached, by the last marker in its log —
    /// last, not first, because the log is a history and the newest line is where
    /// the attempt actually is.</summary>
    public static EaInstallPhase PhaseOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return EaInstallPhase.Unknown;

        var best = EaInstallPhase.Unknown;
        var at = -1;

        void Consider(string marker, EaInstallPhase phase)
        {
            var i = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (i > at)
            {
                at = i;
                best = phase;
            }
        }

        Consider("Registering bundle dependency provider", EaInstallPhase.Registering);
        Consider("Acquiring package", EaInstallPhase.Downloading);
        Consider("OnCacheAcquireBegin", EaInstallPhase.Downloading);
        Consider("Verified acquired payload", EaInstallPhase.Verifying);
        Consider("Applying execute package", EaInstallPhase.Installing);
        Consider("tryStartMsiService", EaInstallPhase.Installing);
        Consider("Failed to install MSI package", EaInstallPhase.Failed);
        Consider("Failed to execute MSI package", EaInstallPhase.Failed);
        Consider("Applied rollback package", EaInstallPhase.Failed);
        return best;
    }

    /// <summary>What the bundle said it is about to cache, in bytes — Burn's own
    /// number for the payload, so the launcher can say "234 MB" instead of nothing
    /// for the minutes it takes to arrive.</summary>
    public static long PackageBytes(string? text)
    {
        const string marker = "dw64PackageCacheSize=";
        if (string.IsNullOrEmpty(text))
            return 0;

        var i = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return 0;

        var start = i + marker.Length;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;

        return long.TryParse(text[start..end], out var bytes) ? bytes : 0;
    }

    /// <summary>The sentence for a phase, or null for phases with nothing worth
    /// saying. The download one exists because that is where a healthy attempt
    /// looks most dead.</summary>
    public static string? PhaseLine(EaInstallPhase phase, long packageBytes) => phase switch
    {
        EaInstallPhase.Registering =>
            "The EA installer has started and is registering itself in the prefix.",
        EaInstallPhase.Downloading => packageBytes > 0
            ? $"The EA installer is downloading its {packageBytes / (1024 * 1024)} MB package from "
              + "EA's CDN. Burn writes nothing to its log while it downloads, so a quiet log here "
              + "is normal — this is not a stall."
            : "The EA installer is downloading its package from EA's CDN. Burn writes nothing to "
              + "its log while it downloads, so a quiet log here is normal — this is not a stall.",
        EaInstallPhase.Verifying =>
            "The download finished and the bundle is checking the package's hash.",
        EaInstallPhase.Installing =>
            "The bundle is applying EA's MSI now — this is the step where Wine's MSI engine does "
            + "the work.",
        EaInstallPhase.Failed =>
            "The bundle has reported a failure and is rolling back.",
        _ => null,
    };

    /// <summary>The lines that say how an attempt ended — the errors, and not the
    /// hundreds of lines around them. Kept as Burn wrote them: the codes are the
    /// diagnosis, and a paraphrase would lose them.</summary>
    static readonly string[] FailureMarkers =
    {
        "e000: Error",
        "logRattError",
        "i319: Applied execute package",
        "i319: Applied rollback package",
    };

    public static IReadOnlyList<string> FailureLines(string? text, int max = 6)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || max <= 0)
            return lines;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;
            var isFailure = false;
            foreach (var marker in FailureMarkers)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    isFailure = true;
                    break;
                }
            }
            if (isFailure && !lines.Contains(line))
                lines.Add(line);
        }

        // The last ones are the ones that ended it.
        return lines.Count <= max ? lines : lines.GetRange(lines.Count - max, max);
    }

    /// <summary>Where the bundle put the MSI's own log, from the variable Burn
    /// sets before it applies the package. Null when the log never named one.</summary>
    public static string? MsiLogPath(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.Contains("WixBundleLog_", StringComparison.Ordinal))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var value = line[(eq + 1)..].Trim();
            // The rollback log is named in the same family and is not this one.
            if (value.EndsWith(".msi.log", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    /// <summary>Whether Wine could not even open its MSI log — <c>MsiEnableLogW
    /// unable to enable log … (32)</c>, error 32 being a sharing violation against
    /// the log file Burn had already created and was holding. When this is in the
    /// child's output, an empty MSI log is a fact about Wine's logging and says
    /// nothing about whether the engine ran. It said nothing on 2026-09-22, and an
    /// earlier version of this file read that silence as "the engine never opened
    /// the package" — which the second attempt's captured stderr refuted.</summary>
    public static bool LogEnableFailed(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains("MsiEnableLogW unable to enable log", StringComparison.Ordinal);

    /// <summary>The action Wine's MSI engine halted on, named as Wine named it:
    /// from <c>err:msi:ITERATE_Actions Execution halted, action L"JunoInitializeSession"
    /// returned 1603</c> this returns <c>JunoInitializeSession</c>. Null when the
    /// engine never said it halted.</summary>
    public static string? FailingAction(string? text)
    {
        const string marker = "Execution halted, action L\"";
        if (string.IsNullOrEmpty(text))
            return null;

        var i = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return null;

        var start = i + marker.Length;
        var end = text.IndexOf('"', start);
        if (end <= start)
            return null;

        var name = text[start..end].Trim();
        // A halt line without a name is not a halt line worth quoting.
        return name.Length == 0 ? null : name;
    }

    /// <summary>The code that action returned, or 0 when no halt line was found.</summary>
    public static int FailingActionCode(string? text)
    {
        const string marker = "\" returned ";
        if (string.IsNullOrEmpty(text))
            return 0;

        var i = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return 0;

        var start = i + marker.Length;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;

        return int.TryParse(text[start..end], out var code) ? code : 0;
    }

    /// <summary>The <c>err:</c> lines worth quoting from the installer's own output,
    /// in order. The tail of that output is the *window* after the failure — progress
    /// bars, accessibility fixmes, the installer's embedded browser complaining about
    /// a missing Trident — so the diagnosis is not in the last forty lines, it is in
    /// these. The browser noise is deliberately not among them: it repeats twenty
    /// times an attempt and would crowd out the lines that matter, which is the exact
    /// way evidence gets lost.</summary>
    static readonly string[] WineMarkers =
    {
        "err:msi:",
        "err:module:",
        "err:environ:init_peb starting",
    };

    /// <summary>The base name of the executable an <c>init_peb</c> line names — the
    /// part that is worth telling apart. Wine starts the same bundle four times from
    /// four extraction directories, and four lines differing only in a GUID are one
    /// fact.</summary>
    static string PebName(string line)
    {
        var start = line.IndexOf("init_peb starting L\"", StringComparison.Ordinal);
        if (start < 0)
            return line;

        start += "init_peb starting L\"".Length;
        var end = line.IndexOf('"', start);
        if (end <= start)
            return line;

        var path = line[start..end];
        var slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    public static IReadOnlyList<string> WineDiagnosticLines(string? text, int max = 10)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text) || max <= 0)
            return kept;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var isPeb = false;
            var keep = false;
            foreach (var marker in WineMarkers)
            {
                if (!line.Contains(marker, StringComparison.Ordinal))
                    continue;
                keep = true;
                isPeb = marker.Contains("init_peb", StringComparison.Ordinal);
                break;
            }
            if (!keep)
                continue;
            if (seen.Add(isPeb ? "peb:" + PebName(line) : line))
                kept.Add(line);
        }

        if (kept.Count <= max)
            return kept;

        // The engine's own lines are the diagnosis; context is what gets dropped for
        // room, never them.
        var room = Math.Max(0, max
            - kept.Count(l => l.Contains("err:msi:", StringComparison.Ordinal)));
        var others = kept.Where(l => !l.Contains("err:msi:", StringComparison.Ordinal)).ToList();
        var tail = others.Count <= room ? others : others.GetRange(others.Count - room, room);
        var chosen = new HashSet<string>(tail, StringComparer.Ordinal);
        foreach (var line in kept)
        {
            if (line.Contains("err:msi:", StringComparison.Ordinal))
                chosen.Add(line);
        }
        return kept.Where(chosen.Contains).ToList();
    }

    /// <summary>What a 1603 means here, from the two accounts that can say: the
    /// bundle's log (which carries the code) and the child's own output (which, when
    /// it was kept, carries Wine's reason). Null when the bundle never reported that
    /// code.
    ///
    /// The first version of this said a zero-byte MSI log meant the engine never
    /// opened the package. The captured stderr of 2026-09-22 04:35 refuted it: the
    /// engine opened the package, ran its actions and halted at EA's own custom
    /// action, while its log stayed empty because <c>MsiEnableLogW</c> could not open
    /// the file Burn was holding. An empty log is a fact about logging.</summary>
    public static string? MsiFailureNote(string? bundleText, long msiLogBytes, string? wineText)
    {
        if (string.IsNullOrEmpty(bundleText)
            || !bundleText.Contains("0x80070643", StringComparison.Ordinal))
            return null;

        const string retry = " Wine's MSI is the build under test here: retry with EA App runtime set "
                           + "to Proton, whose Wine is a different one, or switch the runtime for good.";

        // A written log is a written log: the sharing violation only explains an
        // emptiness, and it does not get to deny bytes that are there.
        var logFact = msiLogBytes > 0
            ? $" The MSI's own log has {msiLogBytes} bytes and holds the action trace."
            : LogEnableFailed(wineText)
                ? " The MSI's own log is empty because Wine could not open it at all — MsiEnableLogW "
                + "failed with error 32, a sharing violation against the file Burn had already created "
                + "and was holding — so that emptiness is about Wine's logging, not about the engine."
                : " The MSI's own log is empty, which on its own says nothing about whether the "
                + "engine ran.";

        if (FailingAction(wineText) is { } action)
            return "Error 0x80070643 is MSI's generic 1603, and the reason is in Wine's own output: "
                 + $"Wine's MSI engine opened the package, ran its actions, and halted at EA's custom "
                 + $"action \"{action}\" (returned {FailingActionCode(wineText)}) — so the failure is "
                 + "inside EA's installer as Wine runs it, not in the handoff into Wine's MSI."
                 + logFact + retry;

        return "Error 0x80070643 is MSI's generic 1603 (\"the installation failed\"), so EA's package "
             + "never installed, and nothing in the captured output names the step that failed."
             + logFact + retry;
    }
}
