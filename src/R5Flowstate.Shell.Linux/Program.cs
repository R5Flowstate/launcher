using Avalonia;
using Avalonia.Headless;
using System;
using System.Linq;

namespace R5Flowstate.Shell.Linux;

sealed class Program
{
    /// <summary>Set when launched with --selftest: App runs headless (Avalonia.Headless),
    /// builds the real MainWindow offscreen to exercise XAML/templates/bindings,
    /// runs the EA-detect logic test, prints PASS/FAIL and exits without a GUI.</summary>
    public static bool SelfTest { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Read-only channel probe: prints what the update server publishes and
        // what the card would say about it, fetching only the channel manifest.
        if (args.Contains("--channel-check"))
        {
            Environment.ExitCode = ChannelCheck.RunAsync(
                ArgValue(args, "--channel-check"),
                ArgValue(args, "--install-path"),
                deep: args.Contains("--deep")).GetAwaiter().GetResult();
            return;
        }

        // Read-only EA probe: the Proton build, the Wine inside it, winetricks, what
        // the prefix holds, and whether anything is listening on EA's loopback ports.
        // Creates nothing — not a prefix, not a settings key.
        //
        // Three lanes that used to sit here are gone with the split runtime:
        // `--launch-ea` (a .desktop entry cannot run `proton run` through a runner
        // that had to bootstrap a Wine prefix first — the entry carries the command
        // line itself now), `--ea-bridge`/`--ea-unbridge` (they symlinked an EA
        // install from the Wine prefix into the game's Proton prefix, and with one
        // prefix EA's own install is in the registry the game already reads), and
        // the `--ea-runtime`/`--ea-prefix` arguments (there is no runtime to pick and
        // no second prefix to name).
        if (args.Contains("--ea-probe"))
        {
            Environment.ExitCode = EaProbeCheck.Run();
            return;
        }

        // The loadscreen art lane, end to end: runs the real decoder inside the
        // prefix against the real install and prints every record it wrote. This is
        // the only way to see art work here without launching a window — and the
        // only way to tell "this map ships no art" from "this install's art cannot
        // be decoded". Read-only on the install and the settings file; it writes
        // the art cache and the art host's own prefix, which is what a decode is.
        if (args.Contains("--art-probe"))
        {
            Environment.ExitCode = ArtProbeCheck.RunAsync(
                ArgValue(args, "--art-probe"),
                ArgValue(args, "--stems"),
                ArgValue(args, "--install-path"),
                ArgValue(args, "--width"),
                ArgValue(args, "--art-paks"),
                all: args.Contains("--all"),
                force: args.Contains("--force"),
                keep: args.Contains("--keep-records")).GetAwaiter().GetResult();
            return;
        }

        // Read-only display probe: what the Res button would offer on this machine,
        // which of the two tools answered, and what each window mode puts on the
        // launch line. Writes nothing and starts nothing — the launch lines are built
        // in memory. This is the resolution half of the Play tab's evidence.
        if (args.Contains("--display-probe"))
        {
            Environment.ExitCode = DisplayProbeCheck.Run(raw: args.Contains("--raw"));
            return;
        }

        // Read-only master-server probe: the browser's list call, the notice, and
        // the download lanes, printing what they say without joining or writing.
        if (args.Contains("--servers"))
        {
            Environment.ExitCode = ServerCheck.RunAsync(
                ArgValue(args, "--install-path")).GetAwaiter().GetResult();
            return;
        }

        // Read-only stats probe: the leaderboard, the seasons, one player card,
        // the match history and the activity line, printing what they say and
        // writing nothing.
        if (args.Contains("--leaderboard"))
        {
            Environment.ExitCode = LeaderboardCheck.RunAsync(
                ArgValue(args, "--sort"),
                ArgValue(args, "--order"),
                ArgValue(args, "--season"),
                ArgValue(args, "--q"),
                ArgValue(args, "--limit")).GetAwaiter().GetResult();
            return;
        }

        // Read-only blog probe: the site feed, one post's markdown as the
        // reader lays it out, and the unread stamp — printing what they say and
        // writing nothing (not even the stamp). --images N additionally pulls
        // N of the post's own pictures through the real loader and prints the
        // size that came back, which is the only way to see (without a window)
        // that the WebP the site serves actually decodes here.
        if (args.Contains("--blog"))
        {
            Environment.ExitCode = BlogCheck.RunAsync(
                ArgValue(args, "--slug"),
                ArgValue(args, "--limit"),
                ArgValue(args, "--images")).GetAwaiter().GetResult();
            return;
        }

        // Real install from the console, same engine path as the card.
        if (args.Contains("--install"))
        {
            Environment.ExitCode = InstallRunner.RunAsync(
                ArgValue(args, "--lane"),
                ArgValue(args, "--install-path"),
                keepLocal: args.Contains("--keep-local")).GetAwaiter().GetResult();
            return;
        }

        // Menu entries, for install.sh: it stages a publish, points the entry at
        // where the binary will live, and only then deletes the staging copy.
        if (args.Contains("--install-shortcuts"))
        {
            Environment.ExitCode = ShortcutRunner.Run(
                ArgValue(args, "--exec"),
                ArgValue(args, "--prefix"),
                ArgValue(args, "--proton"),
                uninstall: args.Contains("--uninstall"));
            return;
        }

        SelfTest = args.Contains("--selftest");
        if (SelfTest)
        {
            // Headless: the same App/MainWindow XAML, no display needed. Never
            // auto-launches a visible window.
            BuildAvaloniaApp()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .StartWithClassicDesktopLifetime(args);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The value after <paramref name="flag"/>, when it is not another flag.</summary>
    static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length)
            return null;
        var value = args[i + 1];
        return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    // DevTools is attached on the window (MainWindow's ctor), not here: Avalonia
    // 11 has no AppBuilder.WithDeveloperTools — that was never valid API.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
