using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using R5Flowstate.Content;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

/// <summary>
/// Windows "Installed apps" removes the launcher only; the downloaded game is a
/// separate folder the uninstaller never sees. This runs on the Velopack
/// uninstall hook and offers to take the game with it.
/// </summary>
public static class UninstallHook
{
    public static void Run()
    {
        try
        {
            var settings = SettingsStore.Load();
            Loc.Initialize(NoticeLanguages.ResolveUi(settings.UiLanguage, settings.EulaLanguage));

            var root = settings.InstallPath?.Trim() ?? string.Empty;
            if (!DirectoryRemover.LooksLikeOurInstall(root))
            {
                ForgetSettings();
                return;
            }

            var answer = MessageBox.Show(
                Loc.Format("uninstall_prompt", Path.GetFullPath(root), DescribeSize(root)),
                Loc.Get("uninstall_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            ForgetSettings();
            StartDetachedRemoval(Path.GetFullPath(root));
        }
        catch
        {
            // The uninstall itself must proceed whatever happens here.
        }
    }

    /// <summary>
    /// The hook has a short budget and the launcher's own files go away under it,
    /// so the game folder is handed to a detached rd. The game folder is never
    /// the folder being uninstalled, so nothing has to wait for us to exit.
    /// </summary>
    static void StartDetachedRemoval(string root)
    {
        // A quoted path is the whole command line here: anything that could end
        // the quote or reach the shell is a path we decline to hand over.
        if (root.IndexOfAny(new[] { '"', '&', '|', '<', '>', '^', '%' }) >= 0)
            return;

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = $"/d /c rd /s /q \"{root}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try { Process.Start(psi); }
        catch { }
    }

    static void ForgetSettings()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(ProductConstants.RegistryKeyPath, throwOnMissingSubKey: false); }
        catch { }
    }

    /// <summary>
    /// Velopack kills this hook after 30 seconds and the player still has to read
    /// the prompt, so the size walk gets a hard budget and gives up rather than
    /// spending the answer time.
    /// </summary>
    static string DescribeSize(string root)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var bytes = 0L;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += f.Length;
                if (clock.ElapsedMilliseconds > 1500)
                    return "over " + Gb(bytes);
            }
            return Gb(bytes);
        }
        catch
        {
            return Loc.Get("uninstall_size_unknown");
        }
    }

    static string Gb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
}
