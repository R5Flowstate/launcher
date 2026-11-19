using System.Diagnostics;
using System.Net.Http;
using R5Flowstate.Contracts;
using Velopack;
using Velopack.Sources;

namespace R5Flowstate.HostGui;

/// <summary>
/// Self-update for the server tool. Checks and downloads in the background;
/// applying always waits for the user because it restarts the tool.
/// A copy run from the server zip (no Update.exe) cannot update itself.
/// </summary>
sealed class ToolUpdate
{
    readonly UpdateManager? _mgr;
    UpdateInfo? _info;

    public ToolUpdate()
    {
        var skip = Environment.GetEnvironmentVariable(ProductConstants.SkipSelfUpdateEnvVar);
        if (Debugger.IsAttached || (!string.IsNullOrWhiteSpace(skip) && skip != "0"))
            return;
        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(ProductConstants.HostToolFeedUrl, new Downloader()));
            _mgr = mgr.IsInstalled ? mgr : null;
        }
        catch (Exception)
        {
            _mgr = null;
        }
    }

    public bool IsInstalled => _mgr is not null;

    public string CurrentVersion =>
        _mgr?.CurrentVersion?.ToString()
        ?? typeof(ToolUpdate).Assembly.GetName().Version?.ToString(3)
        ?? "?";

    /// <summary>Staged version waiting for a restart, or null.</summary>
    public string? ReadyVersion => _mgr?.UpdatePendingRestart?.Version.ToString();

    /// <summary>Checks the feed and downloads a newer build; returns its version or null.</summary>
    public async Task<string?> CheckAndDownloadAsync(Action<int>? percent = null)
    {
        if (_mgr is null)
            return null;
        if (_mgr.UpdatePendingRestart is { } pending)
            return pending.Version.ToString();
        _info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (_info?.TargetFullRelease is null)
            return null;
        await _mgr.DownloadUpdatesAsync(_info, percent).ConfigureAwait(false);
        return _info.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (_mgr?.UpdatePendingRestart is { } pending)
            _mgr.ApplyUpdatesAndRestart(pending);
    }

    sealed class Downloader : HttpClientFileDownloader
    {
        protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
        {
            var http = base.CreateHttpClient(headers, timeout);
            http.DefaultRequestHeaders.Remove("User-Agent");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "R5FlowstateServer/0.2.3");
            return http;
        }
    }
}
