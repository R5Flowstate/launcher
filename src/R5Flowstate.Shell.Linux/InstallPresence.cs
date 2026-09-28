using System;
using System.IO;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// "Is the game here?" — which is what every install-shaped gate in the launcher
/// is really asking, and what <see cref="LinuxSettings.LooksLikeInstall"/>
/// cannot answer on Linux.
///
/// Windows' rule is a marker list: <c>r5apex.exe</c>, <c>r5apex_ds.exe</c>,
/// <c>client.dll</c>. On Windows all three are the game's own files. Here the
/// platform lane — the SDK half of the install, 89 MB, deliberately installable
/// on its own — ships a <c>client.dll</c> of its own into the same root, so a
/// root with the platform and no game satisfies the Windows rule and every gate
/// downstream believes the game is there: the setup card hides itself, PLAY
/// tries to launch a binary that does not exist, and the player is told nothing.
///
/// So the file alone does not decide here. The installer's own record does:
/// INSTALL_STATE.json knows which lanes are on disk, and when the client lane is
/// not ready, the platform's <c>client.dll</c> is not evidence of a game. A root
/// with no record at all is a hand-copied game, where Windows' rule stands.
/// </summary>
static class InstallPresence
{
    /// <summary>The game's binaries are where the launcher can launch them.</summary>
    internal static bool GamePresent(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return false;
        var root = installPath;
        try
        {
            if (File.Exists(Path.Combine(root, "r5apex.exe"))
                || File.Exists(Path.Combine(root, "r5apex_ds.exe")))
                return true;

            // client.dll without a game binary: the platform's own copy, or the
            // game's, and only the install record can tell them apart.
            if (!File.Exists(Path.Combine(root, "client.dll")))
                return false;
            return !ClientLaneNotReady(root);
        }
        catch
        {
            // A path that cannot be read is not a path with a game in it.
            return false;
        }
    }

    /// <summary>True when the install's own record says the client half is not
    /// there. No record, or a record that cannot be read, answers false: an
    /// unreadable bookkeeping file must not hide an install that works.</summary>
    internal static bool ClientLaneNotReady(string installPath)
    {
        try
        {
            var statePath = Path.Combine(installPath, ProductConstants.InstallStateFileName);
            if (!File.Exists(statePath))
                return false;
            return !InstallStateIO.Load(statePath).ClientReady;
        }
        catch
        {
            return false;
        }
    }
}
