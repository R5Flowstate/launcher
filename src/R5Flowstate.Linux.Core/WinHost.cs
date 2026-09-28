using System;

namespace R5Flowstate.Linux.Core;

/// <summary>
/// Where the Windows helpers live: the two exes the launcher runs <em>through
/// Proton</em> rather than as Linux programs — the loadscreen art host
/// (<c>r5f-arthost.exe</c>) and the console relay (<c>r5f-relay.exe</c>).
///
/// <para>One locator, because both are published under <c>winhost/</c> beside the
/// launcher by install.sh and both have to be findable from a development build
/// too. A second copy of this search would be a second thing to keep in step, and
/// this codebase has already paid for that once: the EA log reader and the prefix
/// layout each had their own idea of where a prefix keeps its files, and one of
/// them was blind to the other's prefixes for a release.</para>
///
/// <para><b>Each helper gets a folder of its own</b> — <c>winhost/r5f-arthost/</c>
/// and <c>winhost/r5f-relay/</c> — because both are <em>self-contained trimmed</em>
/// publishes: the trimmer rewrites each app's copy of the framework it uses, so the
/// same file name holds different bytes in the two publishes. install.sh published
/// both into one flat folder for a while on the theory that identical inputs give
/// identical outputs; the relay's copy of <c>System.Console.dll</c> then landed on
/// top of the host's, the host died at its first <c>Console.WriteLine</c>
/// (<c>MissingMethodException</c>, exit 82) before writing a single record, and
/// every map came back as "the art host did not report this one" with a fully
/// decoded picture sitting nowhere. One folder per helper is what makes two
/// trimmed self-contained apps coexist.</para>
///
/// <para>The flat <c>winhost/&lt;exe&gt;</c> shape is still searched, last, so an
/// install made before the split keeps working: a host-only install from that era
/// has a self-consistent framework and runs. An install made <em>after</em> the
/// relay existed has the mixture and needs install.sh re-run — which is what
/// <see cref="ArtDecode"/>'s "did not start" note is about.</para>
///
/// <para>Order, and why: beside the launcher first (the installed shape, no
/// absolute path needed), then the install prefix (a launcher started from
/// somewhere else), and last a published build inside the source tree — which is
/// the only way the read-only probe lanes can run on a development box without
/// installing anything. A shipped launcher never reaches the third: its
/// <see cref="AppContext.BaseDirectory"/> is under <c>~/.local/opt</c>, where no
/// <c>src/</c> exists above it.</para>
/// </summary>
public static class WinHost
{
    public const string FolderName = "winhost";

    /// <summary>The folder beside the launcher, which is where install.sh publishes
    /// to and where both callers look first.</summary>
    public static string Dir() => Path.Combine(AppContext.BaseDirectory.TrimEnd('/'), FolderName);

    /// <summary>The launcher's own install folder.</summary>
    public static string InstallPrefix() => Path.Combine(Home(), ".local", "opt", "r5flowstate");

    /// <summary>
    /// The folder one helper owns, under <c>winhost/</c>: its own file name without
    /// the extension. Derived rather than listed so there is no second place naming
    /// the two helpers — <c>r5f-arthost.exe</c> is <c>winhost/r5f-arthost/</c> by
    /// construction.
    /// </summary>
    public static string FolderFor(string exeName) =>
        Path.GetFileNameWithoutExtension(exeName ?? string.Empty);

    /// <summary>One of the helpers, or null when it is not installed anywhere.</summary>
    public static string? Find(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return null;

        var folder = FolderFor(exeName);

        // Its own folder first, then the flat shape install.sh used before the
        // helpers were split — see the class note for why they cannot share one.
        foreach (var root in new[] { Dir(), Path.Combine(InstallPrefix(), FolderName) })
        {
            var own = Path.Combine(root, folder, exeName);
            if (File.Exists(own))
                return own;

            var flat = Path.Combine(root, exeName);
            if (File.Exists(flat))
                return flat;
        }

        return DevBuild(exeName);
    }

    /// <summary>
    /// The newest published copy of an exe under any project's <c>bin/</c> in this
    /// source tree, or null.
    ///
    /// <para>Newest rather than a fixed path on purpose: the point of it is to run a
    /// probe right after <c>dotnet publish</c> without an install step in between, and
    /// a fixed path names one configuration. <c>bin/</c> only — <c>obj/</c> holds
    /// intermediate copies of the same files, and picking one of those would be
    /// picking a build nobody asked for.</para>
    /// </summary>
    static string? DevBuild(string exeName)
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
            for (var up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
            {
                var src = Path.Combine(dir.FullName, "src");
                if (!Directory.Exists(src))
                    continue;

                var published = new List<string>();
                foreach (var project in Directory.EnumerateDirectories(src))
                {
                    var bin = Path.Combine(project, "bin");
                    if (Directory.Exists(bin))
                        published.AddRange(Directory.GetFiles(bin, exeName, SearchOption.AllDirectories));
                }

                if (published.Count == 0)
                    return null;

                published.Sort((a, b) =>
                    File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                return published[0];
            }
        }
        catch
        {
            // A probe lane's convenience is never worth failing a launch over.
        }
        return null;
    }

    static string Home()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// A helper's PE subsystem: 2 is GUI, 3 is console, and that one field is the
    /// whole difference between a helper that shows a window and one that does not.
    ///
    /// <para>Windows decides whether to allocate a console from this field and
    /// nothing else, and <c>proton run</c> keeps the rule — so a helper built with
    /// <c>OutputType=Exe</c> (subsystem 3) puts a dark Wine console on screen every
    /// time it starts. That is what the player saw when a batch of loadscreens was
    /// decoded, and again for each of the two relays a launch starts. The fix is in
    /// the two csprojs (<c>WinExe</c>); this reader is how that is checked from here,
    /// because nothing else on Linux would notice the field going back.</para>
    ///
    /// <para>Read where the field actually is: the DOS header's <c>e_lfanew</c> at
    /// 0x3C says where the PE header starts, the optional header begins 24 bytes
    /// after that (4 for the signature, 20 for the COFF header), and Subsystem is
    /// 68 bytes into it — the same offset for PE32 and PE32+. So the field is 92
    /// bytes in, which is why the block read below is 0x60 and not the 0x40 a
    /// header-only read would suggest: the two .NET apphosts installed on this box
    /// put <c>e_lfanew</c> at 0xF0, and a 64-byte block from there stops 28 bytes
    /// short of the field.</para>
    ///
    /// <para>Null for anything that is not a readable PE image, which is the
    /// honest answer for a helper that is not installed yet.</para>
    /// </summary>
    public static int? SubsystemOf(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return null;

        // Enough of the PE header to hold the optional header's magic and the
        // subsystem field after it.
        const int PeBlock = 0x60;
        const int SubsystemOffset = 24 + 68;

        try
        {
            using var stream = File.OpenRead(exePath);
            if (stream.Length < 0x40)
                return null;

            var header = new byte[0x40];
            Read(stream, header);
            if (header[0] != 'M' || header[1] != 'Z')
                return null;

            var peOffset = BitConverter.ToInt32(header, 0x3C);
            if (peOffset <= 0 || peOffset > stream.Length - PeBlock)
                return null;

            var pe = new byte[PeBlock];
            stream.Position = peOffset;
            Read(stream, pe);
            if (pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0)
                return null;

            // The optional header's magic (0x10B PE32, 0x20B PE32+) has to be one of
            // the two, or this is not an image whose layout this reader knows.
            var magic = BitConverter.ToUInt16(pe, 24);
            if (magic is not (0x10B or 0x20B))
                return null;

            return BitConverter.ToUInt16(pe, SubsystemOffset);
        }
        catch
        {
            // Missing, unreadable, or not a file at all.
            return null;
        }
    }

    /// <summary>True when the helper is a GUI binary — the shape that allocates no
    /// console window.</summary>
    public static bool IsGuiSubsystem(string? exePath) => SubsystemOf(exePath) == 2;

    static void Read(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0)
                throw new EndOfStreamException("the image ended before its headers did");
            read += got;
        }
    }
}
