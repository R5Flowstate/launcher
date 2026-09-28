using System;
using System.Collections.Generic;
using System.IO;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Writes (or removes) the launcher's menu entries from the console, so the
/// installer script can do it without a window:
///
///     R5Flowstate --install-shortcuts --exec ~/.local/opt/r5flowstate/R5Flowstate
///     R5Flowstate --install-shortcuts --uninstall
///
/// <c>--exec</c> exists because the process that runs this is not always the
/// process that should be pointed at: <c>install.sh</c> calls this from the
/// staging directory it is about to delete, and an entry pointing there would be
/// an entry that does nothing. The launcher's own button passes nothing and gets
/// <see cref="Environment.ProcessPath"/>, which is right for it — the copy the
/// player is looking at is the copy they mean.
///
/// <c>--prefix</c> and <c>--proton</c> default to the settings file. The EA App
/// entry goes into that same prefix, because there is only one now — the
/// <c>--ea-runtime</c>/<c>--ea-prefix</c> arguments went with the split runtime.
/// </summary>
static class ShortcutRunner
{
    public static int Run(string? execPath, string? prefixPath, string? protonDir, bool uninstall)
    {
        Loc.Initialize(null);
        var applications = DesktopShortcuts.ApplicationsDir();

        if (uninstall)
        {
            DesktopShortcuts.RemoveAll();
            Console.WriteLine($"Removed:  {Path.Combine(applications, DesktopShortcuts.LauncherFileName)}");
            Console.WriteLine($"Removed:  {Path.Combine(applications, DesktopShortcuts.EaAppFileName)}");
            Console.WriteLine("Menu entries are gone (missing ones count as gone).");
            return 0;
        }

        var exec = string.IsNullOrWhiteSpace(execPath) ? Environment.ProcessPath : execPath;
        if (string.IsNullOrWhiteSpace(exec))
        {
            Console.WriteLine("FAIL  no --exec given and this process cannot name itself.");
            return 2;
        }
        if (!File.Exists(exec))
        {
            // A menu entry pointing at nothing is the one outcome worth failing on.
            Console.WriteLine($"FAIL  the launcher binary is not there: {exec}");
            return 2;
        }

        var settings = LinuxSettings.Load();
        var prefix = string.IsNullOrWhiteSpace(prefixPath) ? settings.PrefixPath : prefixPath;
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = LinuxSettings.DefaultPrefixPath;
        var proton = string.IsNullOrWhiteSpace(protonDir) ? settings.ProtonDir : protonDir;

        var eaExe = ProtonLauncher.FindEaDesktopExe(prefix);
        var eaPlan = new EaAppShortcut(prefix, eaExe ?? "", proton);

        Console.WriteLine($"Launcher: {exec}");
        Console.WriteLine($"Prefix:   {prefix}");
        Console.WriteLine($"Proton:   {(string.IsNullOrWhiteSpace(proton) ? "(latest found)" : proton)}");
        Console.WriteLine($"EA:       in the game's prefix @ {prefix}");
        Console.WriteLine($"EA exe:   {eaExe ?? "not installed (no EA App entry will be written)"}");
        Console.WriteLine($"Icon:     {AppIcon.Source() ?? "(none: entry uses a generic icon)"}");
        Console.WriteLine();

        var results = DesktopShortcuts.WriteAll(exec, proton, prefix, AppIcon.Source(),
            ea: eaPlan);
        var launcherOk = false;
        foreach (var r in results)
        {
            Console.WriteLine(r.Written
                ? $"written   {r.Path}"
                : $"skipped   {r.Name}: {r.Problem}");
            if (r.Written && r.Name == "launcher")
                launcherOk = true;
        }

        var caches = launcherOk ? DesktopShortcuts.RefreshCaches() : Array.Empty<string>();
        if (caches.Count > 0)
            Console.WriteLine($"refreshed {string.Join(", ", caches)}");
        else
            Console.WriteLine("note      the desktop's index tools are not installed; the entry "
                              + "appears after the next login either way.");

        Console.WriteLine();
        if (!launcherOk)
        {
            Console.WriteLine("FAIL  the launcher's own entry could not be written.");
            return 1;
        }
        // A skipped EA entry is normal before EA App is installed, so it is a
        // note rather than a failure: the launcher's entry is what was asked for.
        var ea = new List<ShortcutWrite>(results).Find(r => r.Name == "EA App");
        if (ea is not null && !ea.Written)
            Console.WriteLine("Note:     no EA App entry yet (" + ea.Problem + "). Re-run after EA is installed.");
        Console.WriteLine("Shortcuts installed.");
        return 0;
    }
}
