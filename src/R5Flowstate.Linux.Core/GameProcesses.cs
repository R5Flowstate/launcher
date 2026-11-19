using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Stopping the game and its dedicated server, the way Windows'
/// <c>ProcessSpawner</c> does it — by image name, scoped to the install root —
/// plus the two things this side has to change.
///
/// <para><b>What it replaces, and why that was the player's bug.</b> The first
/// port killed with <c>pkill -f r5apex_ds.exe</c>. That is three ways to miss at
/// once. The pattern names one image, so a running <em>client</em> was never in it
/// — the console tab's STOP killed the dedi and left the game up, which is exactly
/// what was reported. A pattern is not a scope: <c>-f</c> matches any process whose
/// command line contains the string, including one under another install root, and
/// this box also runs the player's own Proton sessions. And <c>pkill</c> without a
/// signal sends SIGTERM, which is a request — a wineserver mid-teardown, or a
/// process that catches it, keeps running with the UDP port still bound, which is
/// what "doesn't really force the game to close" looks like from outside.</para>
///
/// <para><b>What replaces it.</b> Upstream's rule, read out of <c>/proc</c>: a
/// process is a candidate when its command line names the role's image and either
/// that command line or its working directory is under the install root. Under
/// Proton the whole chain carries the exe argument — proton's own script, the
/// wineserver, the game process itself (<c>wine</c> loads the PE in-process) — so
/// one sweep covers the tree, not just the leaf the launcher happens to hold a
/// handle on. Then, unlike upstream, the signal is escalated: SIGTERM, a grace,
/// then SIGKILL for whatever is still there. Upstream's
/// <c>Kill(entireProcessTree)</c> is already SIGKILL on the handles it owns; this
/// side is also sweeping pids it does not own, where the polite signal is a
/// request that can simply be ignored.</para>
///
/// <para><b>What is deliberately upstream's.</b> A blank install root means no
/// sweep at all (only the children this launcher owns are stopped) — upstream's
/// <c>KillByImageUnderRoot</c> returns zero for a blank root, and the reason is
/// sound: an unscoped sweep would be a licence to kill any Apex on the machine.
/// Both client images are always named together, DX12 included, which is the half
/// of the bug the pattern never had.</para>
/// </summary>
public static class GameProcesses
{
    public const string DediExeName = "r5apex_ds.exe";

    /// <summary>Both client images, so STOP reaches a DX12 session too
    /// (upstream's <c>ClientExeNames</c>).</summary>
    public static readonly string[] ClientExeNames = { "r5apex.exe", "r5apex_dx12.exe" };

    /// <summary>How long a signalled process is given to leave before the signal
    /// is escalated to SIGKILL. Two seconds is a courtesy, not a negotiation: a
    /// wine tree that is going to honour SIGTERM has done so long before this.</summary>
    public const int TermGraceMs = 2000;

    /// <summary>How long a SIGKILLed process is given to be reaped. Long enough
    /// for the kernel to have finished with it, short enough that a STOP with a
    /// wedged child still answers.</summary>
    public const int KillGraceMs = 1000;

    internal const int Sigterm = 15;
    internal const int Sigkill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    /// <summary>The images a role is killed by.</summary>
    public static string[] ImagesFor(LaunchRole role) =>
        role == LaunchRole.Client ? ClientExeNames : new[] { DediExeName };

    /// <summary>One process the sweep is considering: its pid and the command
    /// line it was judged on (kept so a survivor can be re-checked before it is
    /// SIGKILLed — a pid can be reused in the gap, and the newcomer is innocent).</summary>
    internal readonly record struct Candidate(int Pid, string Cmdline);

    // ------------------------------------------------------------ the rules

    /// <summary>
    /// Does this command line name the image? A plain substring search is not
    /// enough: <c>r5apex.exe.bak</c>, <c>r5apex.exe.log</c> and a file merely
    /// called <c>notr5apex_ds.exe</c> all contain the name. So the match has to be
    /// a path leaf — preceded by a separator, a quote, a space, or nothing, and
    /// followed by end-of-string, a space, a quote or a tab.
    /// </summary>
    public static bool NamesImage(string cmdline, string image)
    {
        if (string.IsNullOrEmpty(cmdline) || string.IsNullOrEmpty(image))
            return false;

        var hay = Normalize(cmdline);
        var needle = Normalize(image);
        var from = 0;
        while (from <= hay.Length - needle.Length)
        {
            var at = hay.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
                return false;

            var before = at == 0 ? '\0' : hay[at - 1];
            var afterAt = at + needle.Length;
            var after = afterAt >= hay.Length ? '\0' : hay[afterAt];

            if (before is '\0' or '/' or '"' or '\'' or ' ' or ':' or '=' or '\\'
                && after is '\0' or ' ' or '"' or '\'' or '\t')
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    /// <summary>
    /// Is this process one of the install's? The same question upstream asks with
    /// <c>MainModule.FileName</c>, which has no answer under Proton: the game
    /// process's own image is a Linux wine binary. So the root is looked for where
    /// Proton actually puts it — in the arguments, as the Unix path the launcher
    /// passed or as the <c>Z:\…</c> mapping Wine was handed — and in the process's
    /// working directory, which is the weaker fallback upstream also keeps.
    ///
    /// <para>A blank root is <em>false</em>: upstream refuses to sweep unscoped, and
    /// so does this. <see cref="KillRoles"/> does not even ask in that case.</para>
    /// </summary>
    public static bool IsUnderRoot(string cmdline, string? cwd, string absRoot)
    {
        if (string.IsNullOrWhiteSpace(absRoot))
            return false;

        var root = Normalize(absRoot.Trim().TrimEnd('/'));
        if (root.Length == 0)
            return false;

        var args = Normalize(cmdline);
        if (args.Contains(root + "/", StringComparison.Ordinal))
            return true;

        // The mapping Wine is given for the host filesystem. It cannot equal the
        // Unix form, but a caller passing an already-Windows path would make the
        // two the same — hence the guard rather than a second Contains on a string
        // that was just searched.
        var z = Normalize(PrefixLayout.ToZDrive(root));
        if (z.Length > root.Length && args.Contains(z + "/", StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(cwd))
        {
            var work = Normalize(cwd.Trim().TrimEnd('/'));
            if (work == root || work.StartsWith(root + "/", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Lower-cased, and with Wine's separator folded onto Unix's, so one
    /// comparison covers a path spelled either way.</summary>
    internal static string Normalize(string text) =>
        text.Replace('\\', '/').ToLowerInvariant();

    // -------------------------------------------------------------- the sweep

    /// <summary>
    /// Every process on this machine that is one of the images, under the root.
    /// Read-only, which is what makes it checkable: the suite runs it with an
    /// image name nothing can carry and expects nothing back.
    /// </summary>
    internal static IReadOnlyList<Candidate> Candidates(IReadOnlyList<string> images, string? installRoot)
    {
        var found = new List<Candidate>();
        if (images.Count == 0 || string.IsNullOrWhiteSpace(installRoot))
            return found;

        var mine = Environment.ProcessId;
        string[] entries;
        try { entries = Directory.GetDirectories("/proc"); }
        catch { return found; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!int.TryParse(name, out var pid) || pid <= 2 || pid == mine)
                continue;

            var cmdline = ReadCmdline(entry);
            if (cmdline.Length == 0)
                continue;
            if (!images.Any(image => NamesImage(cmdline, image)))
                continue;
            if (!IsUnderRoot(cmdline, ReadCwdLink(entry), installRoot))
                continue;

            found.Add(new Candidate(pid, cmdline));
        }

        return found;
    }

    static string ReadCmdline(string procDir)
    {
        try
        {
            // NUL-separated arguments; spaces so a leaf boundary is still a
            // boundary for NamesImage.
            return File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ').Trim();
        }
        catch
        {
            // Gone, or not ours to read.
            return string.Empty;
        }
    }

    static string? ReadCwdLink(string procDir)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(Path.Combine(procDir, "cwd"), returnFinalTarget: true);
            var path = target?.FullName;
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            // A process that exited, or one in another user's session.
            return null;
        }
    }

    /// <summary>Is the pid still there? Read from <c>/proc</c> rather than from a
    /// <see cref="Process"/> handle, because most of the sweep is processes this
    /// launcher has no handle on at all.</summary>
    internal static bool Alive(int pid) => Directory.Exists("/proc/" + pid);

    /// <summary>
    /// SIGTERM, a grace, then SIGKILL for what is left — re-checked first, because
    /// the pid may have been reused in the gap and the newcomer is not the game.
    /// Returns how many of the candidates are gone.
    /// </summary>
    internal static int TermThenKill(IReadOnlyList<Candidate> candidates,
        int termGraceMs = TermGraceMs, int killGraceMs = KillGraceMs)
    {
        if (candidates.Count == 0)
            return 0;

        foreach (var candidate in candidates)
            Signal(candidate.Pid, Sigterm);

        var survivors = WaitGone(candidates, termGraceMs);
        if (survivors.Count > 0)
        {
            foreach (var candidate in survivors)
            {
                // Only if it is still the process we judged: a pid that has already
                // been recycled belongs to somebody else by now.
                var now = ReadCmdline("/proc/" + candidate.Pid);
                if (now.Length == 0 || !string.Equals(now, candidate.Cmdline, StringComparison.Ordinal))
                    continue;
                Signal(candidate.Pid, Sigkill);
            }

            WaitGone(survivors, killGraceMs);
        }

        return candidates.Count(c => !Alive(c.Pid));
    }

    static void Signal(int pid, int sig)
    {
        try { kill(pid, sig); }
        catch
        {
            // No pid, no permission, no libc: nothing to do about either.
        }
    }

    /// <summary>Poll until every candidate is gone, or the grace runs out. Returns
    /// the ones still there.</summary>
    static List<Candidate> WaitGone(IReadOnlyList<Candidate> candidates, int graceMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, graceMs);
        while (true)
        {
            var left = candidates.Where(c => Alive(c.Pid)).ToList();
            if (left.Count == 0 || Environment.TickCount64 >= deadline)
                return left;
            System.Threading.Thread.Sleep(25);
        }
    }

    // ------------------------------------------------------------ the callers

    /// <summary>
    /// Stop every process of these roles. <paramref name="tracked"/> is what this
    /// launcher owns outright — the taps PLAY attached, whose children are the
    /// proton processes it started — and they are stopped first, as trees, because
    /// they are the only processes here nobody else has to be consulted about.
    ///
    /// <para>The two halves cannot double-count: a tracked child is a tree kill on
    /// a handle, and the sweep is per pid, so a tree that dies leaves its pids gone
    /// and the sweep finds nothing to add.</para>
    /// </summary>
    public static int KillRoles(IReadOnlyList<LaunchRole> roles, string? installRoot,
        IEnumerable<Process?>? tracked = null)
    {
        var killed = 0;

        foreach (var process in tracked ?? Enumerable.Empty<Process?>())
        {
            if (TryKillTree(process))
                killed++;
        }

        // Upstream's blank-root rule, kept: no root, no sweep.
        if (string.IsNullOrWhiteSpace(installRoot))
            return killed;

        var candidates = new List<Candidate>();
        foreach (var role in roles)
            candidates.AddRange(Candidates(ImagesFor(role), installRoot));

        // One pid can be named by two roles' sweeps (a client launched with the
        // dedi's exe is not a thing, but a duplicated role list is), and killing a
        // pid twice would count it twice.
        var distinct = candidates
            .GroupBy(c => c.Pid)
            .Select(g => g.First())
            .ToList();

        return killed + TermThenKill(distinct);
    }

    /// <summary>Everything both roles, under the root — what STOP does.</summary>
    public static int KillAll(string? installRoot, IEnumerable<Process?>? tracked = null) =>
        KillRoles(new[] { LaunchRole.Client, LaunchRole.Dedicated }, installRoot, tracked);

    static bool TryKillTree(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            if (process.HasExited)
                return false;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(KillGraceMs);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
