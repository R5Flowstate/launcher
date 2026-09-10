using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;
using Velopack;
using Velopack.Sources;

namespace R5Flowstate.Shell;

/// <summary>
/// Velopack self-update. Fail-open. Applies as soon as the shell is idle
/// (no content install, no live game). Does not block Play or INSTALL.
/// </summary>
static class ShellSelfUpdate
{
    static readonly object Gate = new();
    static UpdateManager? s_mgr;
    static int s_busy;
    static int s_appliedOnExit;

    public enum State
    {
        None,
        Available,
        PendingRestart,
    }

    public static async Task TryAsync(
        string? installPath,
        Func<bool> installBusy,
        Action<string> log,
        Action<State, string?>? onState = null)
    {
        if (Debugger.IsAttached)
            return;
        var skip = Environment.GetEnvironmentVariable(ProductConstants.SkipSelfUpdateEnvVar);
        if (!string.IsNullOrWhiteSpace(skip) && skip != "0")
            return;

        if (Interlocked.CompareExchange(ref s_busy, 1, 0) != 0)
            return;

        try
        {
            if (!HasUpdateExe())
                return;

            var mgr = ManagerOrNull();
            if (mgr is not null)
            {
                var pending = mgr.UpdatePendingRestart;
                if (pending is not null)
                {
                    onState?.Invoke(State.PendingRestart, pending.Version.ToString());
                    return;
                }
            }

            var downloader = new R5fWebDownloader();
            UpdateInfo? info = null;
            try
            {
                mgr = InstalledManager(
                    new GithubSource(ProductConstants.GitHubUrl, null, false, downloader));
                if (mgr is not null)
                    info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log("GitHub update check failed, trying CDN: " + ex.Message);
                mgr = null;
                info = null;
            }
            if (mgr is null)
            {
                mgr = InstalledManager(
                    new SimpleWebSource(ProductConstants.DefaultLauncherFeedUrl, downloader));
                if (mgr is null)
                    return;
                info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            }

            lock (Gate)
                s_mgr = mgr;

            if (info?.TargetFullRelease is null)
            {
                onState?.Invoke(State.None, null);
                return;
            }

            var ver = info.TargetFullRelease.Version.ToString();
            log("Shell update " + ver);
            onState?.Invoke(State.Available, ver);
            await mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            log("Shell update " + ver + " staged; applies next time you close the launcher.");
            onState?.Invoke(State.PendingRestart, ver);
        }
        catch (Exception ex)
        {
            log("Shell self-update skipped: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref s_busy, 0);
        }
    }

    /// <summary>
    /// Hand the staged update to Update.exe and let it apply once this process
    /// exits. Call on shutdown: yanking the window away mid-session to install
    /// a launcher update is not worth the interruption.
    /// </summary>
    public static void ApplyOnExit(Action<string> log)
    {
        if (Interlocked.CompareExchange(ref s_appliedOnExit, 1, 0) != 0)
            return;
        UpdateManager? mgr;
        lock (Gate)
            mgr = s_mgr;
        var pending = mgr?.UpdatePendingRestart;
        if (mgr is null || pending is null)
            return;
        try
        {
            log("Applying shell update " + pending.Version + " on exit.");
            mgr.WaitExitThenApplyUpdates(pending);
        }
        catch (Exception ex)
        {
            log("Shell update on exit skipped: " + ex.Message);
        }
    }

    public static bool TryApplyIfIdle(
        string? installPath,
        Func<bool> installBusy,
        Action<string> log)
    {
        UpdateManager? mgr;
        lock (Gate)
            mgr = s_mgr;
        if (mgr is null)
            return false;

        if (installBusy())
            return false;

        if (ProcessSpawner.IsRoleAlive(LaunchRole.Client, installPath) ||
            ProcessSpawner.IsRoleAlive(LaunchRole.Dedicated, installPath))
            return false;

        var pending = mgr.UpdatePendingRestart;
        if (pending is null)
            return false;

        log("Restarting to apply shell update " + pending.Version);
        SingleInstance.Release();
        mgr.ApplyUpdatesAndRestart(pending);
        return true;
    }

    static UpdateManager? ManagerOrNull()
    {
        lock (Gate)
            return s_mgr;
    }

    static bool HasUpdateExe()
    {
        var dir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(dir, "Update.exe")) ||
            File.Exists(Path.GetFullPath(Path.Combine(dir, "..", "Update.exe")));
    }

    static UpdateManager? InstalledManager(GithubSource source)
    {
        var mgr = new UpdateManager(source);
        return mgr.IsInstalled ? mgr : null;
    }

    static UpdateManager? InstalledManager(SimpleWebSource source)
    {
        var mgr = new UpdateManager(source);
        return mgr.IsInstalled ? mgr : null;
    }

    sealed class R5fWebDownloader : HttpClientFileDownloader
    {
        protected override HttpClient CreateHttpClient(
            IDictionary<string, string>? headers,
            double timeout)
        {
            var http = base.CreateHttpClient(headers, timeout);
            http.DefaultRequestHeaders.Remove("User-Agent");
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", ProductConstants.ProductName + "/0.1");
            return http;
        }
    }
}
