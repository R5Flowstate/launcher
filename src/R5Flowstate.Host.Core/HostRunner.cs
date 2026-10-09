using R5Flowstate.Content;

namespace R5Flowstate.Host;

/// <summary>
/// Foreground host: every configured instance under one supervisor each, plus
/// a periodic CHANNEL check. An update warns players in chat, waits out the
/// grace, stops every instance, reconciles, and starts them again.
/// </summary>
public sealed class HostRunner
{
    readonly string _installPath;
    readonly Action<string> _log;

    public event Action<DediEvent>? Event;

    public HostRunner(string installPath, Action<string> log)
    {
        _installPath = installPath;
        _log = log;
    }

    public async Task RunAsync(IReadOnlyCollection<string>? only, bool autoUpdate, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var config = HostConfig.Load(_installPath);
            var instances = config.Instances
                .Where(i => only is null || only.Count == 0 || only.Contains(i.Name))
                .ToList();
            if (instances.Count == 0)
                throw new InvalidOperationException("No instances configured. Add one with: r5f-host instance add <name>");

            foreach (var inst in instances)
            {
                var cfg = InstanceCfg.PathFor(_installPath, inst);
                if (!File.Exists(cfg))
                    InstanceCfg.Write(_installPath, inst, new InstanceCfg.Secrets { RconPassword = InstanceCfg.NewRconPassword() });
            }

            using var round = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var supervisors = instances.Select(i => new DediSupervisor(_installPath, config, i)).ToList();
            foreach (var s in supervisors)
                s.Event += e => Event?.Invoke(e);
            var runs = supervisors.Select(s => s.RunAsync(round.Token)).ToList();

            var updateDue = false;
            if (autoUpdate)
            {
                var watcher = WatchForUpdateAsync(config, supervisors, round.Token);
                var done = await Task.WhenAny(Task.WhenAll(runs), watcher).ConfigureAwait(false);
                updateDue = done == watcher && await watcher.ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAll(runs).ConfigureAwait(false);
            }

            foreach (var s in supervisors)
                await s.StopAsync().ConfigureAwait(false);
            round.Cancel();
            try
            {
                await Task.WhenAll(runs).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            foreach (var s in supervisors)
                s.Dispose();

            if (!updateDue)
                return;

            using var fetcher = new FileSystemFetcher();
            _log("updating");
            var check = await HostInstaller.UpdateAsync(_installPath, config, fetcher, null, false, cancel).ConfigureAwait(false);
            _log("updated to " + Describe(check));
            using (var mods = new HostMods(_installPath))
                await mods.SyncAsync(new Progress<string>(_log), cancel).ConfigureAwait(false);
        }
    }

    /// <summary>True once an update is due and the grace has run out.</summary>
    async Task<bool> WatchForUpdateAsync(HostConfig config, IReadOnlyList<DediSupervisor> sups, CancellationToken cancel)
    {
        var every = TimeSpan.FromMinutes(Math.Clamp(config.UpdateCheckMinutes, 5, 24 * 60));
        while (true)
        {
            await Task.Delay(every, cancel).ConfigureAwait(false);
            HostUpdateCheck check;
            try
            {
                using var fetcher = new FileSystemFetcher();
                check = await HostInstaller.CheckAsync(_installPath, config, fetcher, cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log("update check failed: " + ex.Message);
                continue;
            }
            if (!check.UpdateAvailable)
                continue;

            var grace = Math.Clamp(config.UpdateGraceMinutes, 0, 60);
            _log("update available (" + Describe(check) + "); restarting in " + grace + " min");
            for (var left = grace; left > 0; left--)
            {
                foreach (var s in sups)
                    s.SendCommand("say Server restarts for an update in " + left + " minute" + (left == 1 ? "" : "s"));
                await Task.Delay(TimeSpan.FromMinutes(1), cancel).ConfigureAwait(false);
            }
            return true;
        }
    }

    public static string Describe(HostUpdateCheck check) =>
        string.Join(", ", check.Tracks.Select(t => t.Track + " " + t.Tip.CatalogVersion))
        + " gate " + check.Channel.EffectiveGateName;
}
